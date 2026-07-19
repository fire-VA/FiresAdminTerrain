using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Lifecycle;

namespace VerdantsAscent.Terrain.Voxel
{
    // Voxel P0 — Infinity Hammer-compatible brush commands, wired to hoe-tab
    // tools in Docs/infinity_tools.yaml (same RulerArgs parsing as the water
    // and tile brushes: IH `from=` center wins, `circle=` radius, `height=`
    // vertical offset — ruler coordinates VERBATIM, no correction).
    public static class VoxelBrushCommands
    {
        public static void Register()
        {
            new Terminal.ConsoleCommand("va_voxel_carve",
                "[va_voxel_carve radius=3 height=0] Carve true 3D terrain at the ruler. height=0 → sphere; height>0 → cylinder of that total height (shaft/trench). P0: runtime only.",
                args => Apply(args, VoxelBrushMode.Carve));

            new Terminal.ConsoleCommand("va_voxel_fill",
                "[va_voxel_fill radius=3 height=0] Fill solid voxel rock at the ruler. height=0 → sphere; height>0 → cylinder of that total height (pillar/wall). P0: runtime only.",
                args => Apply(args, VoxelBrushMode.Fill));

            new Terminal.ConsoleCommand("va_voxel_delete",
                "[va_voxel_delete radius=4 height=0] Remove voxel edits in the ruler shape — restores the original ground; fully-cleaned chunks vanish entirely.",
                args => Apply(args, VoxelBrushMode.Restore));

            new Terminal.ConsoleCommand("va_voxel_cube",
                "[va_voxel_cube] Stamp a voxel CUBE at the 3D gizmo (Ctrl+Scroll = size, hold LeftAlt = carve, drag to paint).",
                args => ApplyGizmo(args, VoxelBrushShape.Cube));

            new Terminal.ConsoleCommand("va_voxel_sphere",
                "[va_voxel_sphere] Stamp a voxel SPHERE at the 3D gizmo (Ctrl+Scroll = size, hold LeftAlt = carve, drag to paint — dragged spheres make smooth tubes).",
                args => ApplyGizmo(args, VoxelBrushShape.Sphere));

            // Water survey — walk a radius around the player, classify every
            // generated water body by size/shape, and report each one's level
            // range and how it sits against its banks. This is the tool for
            // giving per-body feedback ("that pond is 0.4m proud on its east
            // bank") instead of eyeballing screenshots.
            new Terminal.ConsoleCommand("va_water_scan",
                "[va_water_scan radius=256 step=2] Survey generated water near you: streams, rivers, ponds and lakes, each with its level range, depth, and worst stand-above-bank. Use it to check leveling per body.",
                args =>
                {
                    float radius = 256f, step = 2f;
                    for (int i = 1; i < args.Length; i++)
                    {
                        var a = args[i];
                        int eq = a.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = a.Substring(0, eq).ToLowerInvariant();
                        float.TryParse(a.Substring(eq + 1), out float v);
                        if (k == "radius" && v > 0f) radius = Mathf.Clamp(v, 32f, 1024f);
                        else if (k == "step" && v > 0f) step = Mathf.Clamp(v, 1f, 16f);
                    }
                    foreach (var line in VoxelWaterSurvey.Run(radius, step))
                        args.Context.AddString(line);
                });

            new Terminal.ConsoleCommand("va_voxel_wrapall",
                "[va_voxel_wrapall] Burst-wrap every currently loaded zone to voxel terrain (VoxelWrap mode).",
                args =>
                {
                    int n = VoxelZoneStreamer.WrapAllNow();
                    args.Context.AddString($"va_voxel_wrapall → started {n} zone wrap(s).");
                });

            new Terminal.ConsoleCommand("va_voxel_seamscan",
                "[va_voxel_seamscan] Audit chunk-border DATA divergence. 'va_voxel_seamscan heal' re-mirrors every ring AND remeshes every chunk — data can be converged while a mesh is stale, so heal rebuilds unconditionally.",
                args =>
                {
                    bool heal = args.Length > 1 && args[1].ToLowerInvariant() == "heal";
                    int scanned = 0, dirtyChunks = 0, samples = 0, remeshed = 0;
                    var chunks = new System.Collections.Generic.List<VoxelChunk>(VoxelWorld.AllChunks);
                    foreach (var c in chunks)
                    {
                        if (c.PendingGenerate) continue;
                        scanned++;
                        int n2 = c.MirrorBordersNow(heal);
                        if (n2 > 0)
                        {
                            dirtyChunks++;
                            samples += n2;
                            Debug.Log($"[FiresVoxel] seamscan: chunk {c.Key} {(heal ? "HEALED" : "diverges")} {n2} border sample(s).");
                        }
                        // Heal remeshes EVERY chunk, not just data-dirty ones:
                        // a seam can be a stale mesh built before an earlier
                        // heal, which a data-only scan reports as clean.
                        if (heal)
                        {
                            VoxelMeshPipeline.EnqueueRemesh(c);
                            remeshed++;
                        }
                    }
                    string msg = heal
                        ? $"va_voxel_seamscan heal → {scanned} chunk(s): {dirtyChunks} had divergent border data ({samples} sample(s)); ALL {remeshed} remeshed."
                        : $"va_voxel_seamscan → {scanned} chunk(s): {dirtyChunks} with divergent border DATA ({samples} sample(s)). " +
                          "A visible seam with 0 divergence = stale mesh or quilt — run 'va_voxel_seamscan heal' to rebuild everything.";
                    args.Context.AddString(msg);
                    Debug.Log($"[FiresVoxel] {msg}");
                });

            Debug.Log("[FiresVoxel] va_voxel_carve/fill/delete/cube/sphere registered.");
        }

        private static void Apply(Terminal.ConsoleEventArgs args, VoxelBrushMode mode)
        {
            string cmd = mode == VoxelBrushMode.Fill ? "va_voxel_fill"
                : mode == VoxelBrushMode.Carve ? "va_voxel_carve" : "va_voxel_delete";
            if (FiresVoxelConfig.Enabled == null || !FiresVoxelConfig.Enabled.Value)
            {
                args.Context.AddString($"{cmd}: VoxelTerrain is disabled in the FAT config.");
                return;
            }
            if (!RulerArgs.TryResolveCenter(args, out var center, out _))
            {
                args.Context.AddString($"{cmd} FAILED: could not resolve center (no IH ruler position, cursor raycast missed).");
                return;
            }

            float radius = RulerArgs.ResolveRadius(args, defaultRadius: mode == VoxelBrushMode.Restore ? 4f : 3f);

            // Terrain tools mean "the ground I'm pointing at". The ruler's Y
            // can ride a collider that is NOT the voxel skin (IH tool height
            // offsets, stale meshes) — field logs showed discs centered 5m in
            // the air (density −100 at center) carving nothing. Snap to the
            // field's own surface crossing when one exists nearby; the delta
            // log names how far off the ruler rode.
            if (VoxelWorld.TryFindSurfaceY(center.x, center.z, center.y, 12f, out float snapY)
                && Mathf.Abs(snapY - center.y) > 0.75f)
            {
                Debug.Log($"[FiresVoxel] {cmd} center Y {center.y:F1} → {snapY:F1} " +
                          $"(snapped to the voxel surface; ruler rode {center.y - snapY:+0.0;-0.0}m off it).");
                center.y = snapY;
            }

            // FLAT RULER tools draw flattened cylinders (user spec): no height
            // → a 2m-thick disc at the aim point. Height set via PgUp/PgDn →
            // cylinder spanning FROM the aim point TO the ruler ring (PgUp =
            // pillar to the ring, PgDn = shaft down to it). 3D spheres/cubes
            // live on the Vsphere/Vcube gizmo tools.
            float signedH = 0f;
            RulerArgs.TryParseNamedFloat(args, "height", out signedH);
            float height = Mathf.Abs(signedH);
            if (height > 0.01f) center.y += signedH * 0.5f;
            else height = 2f;

            // Arm the drag pump so holding LMB keeps painting this brush.
            VoxelBrushDrag.RecordCylinder(mode, radius, signedH);

            try
            {
                string summary = VoxelWorld.ApplyBrush(center, radius, height, VoxelBrushShape.Cylinder, mode);
                args.Context.AddString($"{cmd} → {summary}");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"{cmd} FAILED: {ex.Message}");
                Debug.LogError($"[FiresVoxel] {cmd} threw: {ex}");
            }
        }

        // Gizmo tools: the command stamps ONCE at the ruler point (size/mode
        // from the live gizmo state); the drag pump paints from there.
        private static void ApplyGizmo(Terminal.ConsoleEventArgs args, VoxelBrushShape shape)
        {
            string cmd = shape == VoxelBrushShape.Cube ? "va_voxel_cube" : "va_voxel_sphere";
            if (FiresVoxelConfig.Enabled == null || !FiresVoxelConfig.Enabled.Value)
            {
                args.Context.AddString($"{cmd}: VoxelTerrain is disabled in the FAT config.");
                return;
            }
            if (!RulerArgs.TryResolveCenter(args, out var center, out _))
            {
                args.Context.AddString($"{cmd} FAILED: could not resolve center.");
                return;
            }

            var mode = Input.GetKey(KeyCode.LeftAlt) ? VoxelBrushMode.Carve : VoxelBrushMode.Fill;
            try
            {
                string summary = VoxelWorld.ApplyBrush(center, VoxelBrushDrag.EffectiveGizmoRadius(), 0f, shape, mode);
                args.Context.AddString($"{cmd} → {summary}");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"{cmd} FAILED: {ex.Message}");
                Debug.LogError($"[FiresVoxel] {cmd} threw: {ex}");
            }
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Terminal_InitTerminal_RegisterVoxelCommands
        {
            private static bool s_registered;

            [HarmonyPostfix]
            private static void Postfix()
            {
                if (s_registered) return;
                s_registered = true;
                Register();
                VoxelSeamAudit.Register();
                WaterCensus.Register();
                VoxelBrushDrag.EnsureAttached();
                VoxelCaveEnv.EnsureAttached();
            }
        }
    }
}
