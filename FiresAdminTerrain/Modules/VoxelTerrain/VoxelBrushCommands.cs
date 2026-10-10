using System.Collections.Generic;
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
        private static void PinGenWater(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null) { args.Context.AddString("va_gen_water: no local player."); return; }

            // Pins SAVE with the character, so every earlier run's pins stay on
            // the map forever and there is no telling a stale one from a new
            // one. `clear` drops ours before a fresh pass.
            if (args.Length > 1 && args[1].ToLowerInvariant() == "clear")
            {
                args.Context.AddString($"va_gen_water: removed {ClearGenWaterPins()} generated-water pin(s).");
                return;
            }

            if (!VoxelHydrology.Ready)
            {
                args.Context.AddString("va_gen_water: hydrology has not finished building (or Terrain Mode / Hydrology is off).");
                return;
            }

            float radius = 600f;
            if (args.Length > 1 && float.TryParse(args[1], out float r)) radius = Mathf.Clamp(r, 64f, 5000f);
            var pos = player.transform.position;

            int channelPins = VoxelRiverNetwork.PinNear(pos, radius, 120f);

            int lakePins = 0;
            if (Minimap.instance != null)
            {
                var seen = new List<Vector3>();
                // A bowl is as small as 11m across, so a 20m scan step can step
                // clean over one.
                for (float z = -radius; z <= radius; z += 10f)
                    for (float x = -radius; x <= radius; x += 10f)
                    {
                        if (x * x + z * z > radius * radius) continue;
                        float wx = pos.x + x, wz = pos.z + z;
                        // Only pin water that can actually render: above the sea
                        // plane, and deep enough to hold. A pin for water the
                        // renderer skips is worse than no pin.
                        if (!VoxelLakeBasins.TryWaterAt(wx, wz, out float lvl, out float bowlDepth)) continue;
                        if (bowlDepth < 0.5f) continue;
                        bool near = false;
                        for (int i = 0; i < seen.Count && !near; i++)
                            if ((seen[i].x - wx) * (seen[i].x - wx) + (seen[i].z - wz) * (seen[i].z - wz) < 6400f) near = true;
                        if (near) continue;
                        var p = new Vector3(wx, lvl, wz);
                        seen.Add(p);
                        Minimap.instance.AddPin(p, Minimap.PinType.Icon3, "Lake", false, false);
                        lakePins++;
                    }
            }

            args.Context.AddString($"va_gen_water: {channelPins} channel pin(s) + {lakePins} lake pin(s) within {radius:0}m.");
            args.Context.AddString($"  world: {VoxelRiverNetwork.ReachCount} reach(es), {VoxelRiverNetwork.NodeCount} node(s), {VoxelRiverNetwork.ZoneCount} water zone(s), {VoxelHydrology.SpringCount} spring(s).");
        }

        private static int ClearGenWaterPins()
        {
            if (Minimap.instance == null) return 0;
            var pins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins")(Minimap.instance);
            if (pins == null) return 0;
            var ours = new List<Minimap.PinData>();
            for (int i = 0; i < pins.Count; i++)
            {
                string n = pins[i].m_name;
                if (n == "Lake" || n == "River" || n == "Stream") ours.Add(pins[i]);
            }
            for (int i = 0; i < ours.Count; i++) Minimap.instance.RemovePin(ours[i]);
            return ours.Count;
        }

        public static void Register()
        {
            new Terminal.ConsoleCommand("va_gen_water",
                "[va_gen_water radius=600 | va_gen_water clear] Pin the generated rivers, streams and lakes near you on the minimap and report what the hydrology solved. `clear` removes the pins we added (they save with the character, so stale ones outlive the build that made them).",
                args => PinGenWater(args));

            new Terminal.ConsoleCommand("va_voxel_carve",
                "[va_voxel_carve radius=3 height=0] Carve true 3D terrain at the ruler. height=0 → sphere; height>0 → cylinder of that total height (shaft/trench). P0: runtime only.",
                args => Apply(args, VoxelBrushMode.Carve), isCheat: true);

            new Terminal.ConsoleCommand("va_voxel_fill",
                "[va_voxel_fill radius=3 height=0] Fill solid voxel rock at the ruler. height=0 → sphere; height>0 → cylinder of that total height (pillar/wall). P0: runtime only.",
                args => Apply(args, VoxelBrushMode.Fill), isCheat: true);

            new Terminal.ConsoleCommand("va_voxel_delete",
                "[va_voxel_delete radius=4 height=0] Remove voxel edits in the ruler shape — restores the original ground; fully-cleaned chunks vanish entirely.",
                args => Apply(args, VoxelBrushMode.Restore), isCheat: true);

            new Terminal.ConsoleCommand("va_voxel_cube",
                "[va_voxel_cube] Stamp a voxel CUBE at the 3D gizmo (Ctrl+Scroll = size, hold LeftAlt = carve, drag to paint).",
                args => ApplyGizmo(args, VoxelBrushShape.Cube), isCheat: true);

            new Terminal.ConsoleCommand("va_voxel_sphere",
                "[va_voxel_sphere] Stamp a voxel SPHERE at the 3D gizmo (Ctrl+Scroll = size, hold LeftAlt = carve, drag to paint — dragged spheres make smooth tubes).",
                args => ApplyGizmo(args, VoxelBrushShape.Sphere), isCheat: true);

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

            // Water-field seam audit. va_voxel_seamscan audits VOXEL CHUNK data
            // and cannot see this — the water field is a separate per-zone build
            // with its own lattice and apron, so a water seam scans terrain-clean.
            new Terminal.ConsoleCommand("va_water_probe",
                "[va_water_probe] Ground truth at YOUR position: build version, raw-field wet?, reconciled level/depth, class, swim, paint wet01 — proves which layer disagrees.",
                args =>
                {
                    var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    bool has = GenWaterField.TryWaterSurfaceSmooth(p.x, p.z, out float lvl, out float dep);
                    byte wc = GenWaterField.WaterClassAt(p.x, p.z);
                    bool rawWet = GenWaterField.IsWetAt(p.x, p.z);
                    float wet01 = VoxelZoneStreamer.PaintWet01(p.x, p.z);
                    float swim = VoxelWaterSurface.SurfaceAt(p);
                    args.Context.AddString($"va_water_probe @({p.x:0},{p.y:0},{p.z:0})  build v{VerdantsAscent.FiresAdminTerrain.PluginVersion}");
                    args.Context.AddString($"  reconciled: {(has ? $"level={lvl:0.00} depth={dep:0.00}" : "NO SURFACE (dry/cut)")}   class={wc} (0none 1ocean 2river 4lake 5pond 6swamp 7frozen 8hot)");
                    args.Context.AddString($"  rawFieldWet={rawWet}   swim={(swim > -9000f ? swim.ToString("0.00") : "none")}   paint wet01={wet01:0.00}");
                    // The ACTUAL mask texel under the player — stand on each
                    // side of a hard paint edge and compare: if wet01 matches
                    // but the texel differs, the blend path is broken; if the
                    // texel matches while the GROUND looks different, the
                    // disagreement is downstream (shader/clutter), not paint.
                    var hm = Heightmap.FindHeightmap(p);
                    if (hm != null)
                    {
                        var tex = HarmonyLib.AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask")(hm);
                        int hw = HarmonyLib.AccessTools.FieldRefAccess<Heightmap, int>("m_width")(hm);
                        if (tex != null)
                        {
                            int vx = Mathf.Clamp(Mathf.RoundToInt(p.x - (hm.transform.position.x - hw * 0.5f)), 0, hw);
                            int vz = Mathf.Clamp(Mathf.RoundToInt(p.z - (hm.transform.position.z - hw * 0.5f)), 0, hw);
                            var c = tex.GetPixel(vx, vz);
                            args.Context.AddString($"  mask texel[{vx},{vz}] r={c.r:0.00} g={c.g:0.00} b={c.b:0.00} a={c.a:0.00}   (r=dirt, a<1=cleared)");
                        }
                        else args.Context.AddString("  mask texel: paint mask texture is NULL");
                    }
                    else args.Context.AddString("  mask texel: no heightmap loaded here");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("va_skyislands",
                "[va_skyislands [pin|clear]] List generated sky islands nearest first; 'pin' marks EVERY island on the map (portal icon, click-teleport for testing), 'clear' removes the marks.",
                args =>
                {
                    string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (mode == "clear")
                    {
                        args.Context.AddString($"va_skyislands: cleared {FiresMapMarks.Clear()} map mark(s).");
                        return;
                    }
                    if (mode == "pin")
                    {
                        var pos = new System.Collections.Generic.List<Vector3>();
                        var rad = new System.Collections.Generic.List<float>();
                        var kind = new System.Collections.Generic.List<string>();
                        FiresSkyIslands.Snapshot(pos, rad, kind);
                        for (int i = 0; i < pos.Count; i++)
                            FiresMapMarks.Add(pos[i], Minimap.PinType.Icon4, $"Isle{i + 1} {kind[i]}");
                        args.Context.AddString($"va_skyislands: pinned {pos.Count} island(s) on the map ('va_skyislands clear' removes them).");
                        return;
                    }
                    FiresSkyIslands.PrintIslands(args.Context);
                },
                isCheat: true);

            // Water-body finder — pins every body of a class on the MAP so
            // testing is click-teleport, not coordinate guessing.
            new Terminal.ConsoleCommand("va_water_find",
                "[va_water_find frozen|hotspring|lake|pond|swamp|clear] Mark every generated water body of that class on the map (hotsprings = fire icon). 'clear' removes all survey marks.",
                args =>
                {
                    string what = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (what == "clear")
                    {
                        args.Context.AddString($"va_water_find: cleared {FiresMapMarks.Clear()} map mark(s).");
                        return;
                    }
                    byte cls;
                    Minimap.PinType icon;
                    string label;
                    switch (what)
                    {
                        case "frozen": cls = FiresWorldGenBase.WC_FROZEN; icon = Minimap.PinType.Icon3; label = "Frozen"; break;
                        case "hotspring": cls = FiresWorldGenBase.WC_HOTSPRING; icon = Minimap.PinType.Icon0; label = "Spring"; break;
                        case "lake": cls = FiresWorldGenBase.WC_LAKE; icon = Minimap.PinType.Icon3; label = "Lake"; break;
                        case "pond": cls = FiresWorldGenBase.WC_POND; icon = Minimap.PinType.Icon3; label = "Pond"; break;
                        case "swamp": cls = FiresWorldGenBase.WC_SWAMP; icon = Minimap.PinType.Icon3; label = "SwampWater"; break;
                        default:
                            args.Context.AddString("va_water_find: pick frozen | hotspring | lake | pond | swamp | clear");
                            return;
                    }
                    var bodies = new System.Collections.Generic.List<Vector3>();
                    var cells = new System.Collections.Generic.List<int>();
                    int found = FiresWorldGenBase.FindWaterBodies(cls, bodies, cells, 400);
                    for (int i = 0; i < found; i++)
                        FiresMapMarks.Add(bodies[i], icon, $"{label}{i + 1} ({cells[i] * 64}m2)");
                    args.Context.AddString(found == 0
                        ? $"va_water_find: no {what} bodies in the class grid."
                        : $"va_water_find: pinned {found} {what} bod{(found == 1 ? "y" : "ies")} on the map ('va_water_find clear' removes them).");
                },
                isCheat: true);

            // FEATURE TOUR — one command to pin every kind of generated
            // terrain feature on the map for polishing (user: "a way to mark
            // rivers, brooks, streams and all our other special features so I
            // can find them all and inspect them"). Rivers/streams/still-water
            // ride the water-class grid; swampland is the biome DISTRICT;
            // volcanos/sky islands have their own snapshots.
            new Terminal.ConsoleCommand("va_features",
                "[va_features [pin] list|clear|all|<kind>] Pin generated features on the map. kinds: river stream pond lake swampwater swampland frozen hotspring volcano skyisland all. 'list' = counts, 'clear' = remove marks. 'pin' is optional (va_features pin all == va_features all).",
                args =>
                {
                    // 'pin' is an optional verb: va_features pin all / pin river
                    // read as naturally as va_features all / river.
                    int ai = 1;
                    if (args.Length > 1 && args[1].ToLowerInvariant() == "pin") ai = 2;
                    string what = args.Length > ai ? args[ai].ToLowerInvariant() : "list";
                    if (what == "clear")
                    { args.Context.AddString($"va_features: cleared {FiresMapMarks.Clear()} map mark(s)."); return; }

                    var pos = new System.Collections.Generic.List<Vector3>();
                    var cells = new System.Collections.Generic.List<int>();
                    int total = 0;

                    // (kind, class-or-biome, isBiome, icon, label, minCells)
                    (string k, byte id, bool biome, Minimap.PinType icon, string lbl, int min)[] kinds =
                    {
                        ("river",     FiresWorldGenBase.WC_RIVER,     false, Minimap.PinType.Icon3, "River",  40),
                        ("stream",    FiresWorldGenBase.WC_STREAM,    false, Minimap.PinType.Icon3, "Stream", 12),
                        ("pond",      FiresWorldGenBase.WC_POND,      false, Minimap.PinType.Icon3, "Pond",   1),
                        ("lake",      FiresWorldGenBase.WC_LAKE,      false, Minimap.PinType.Icon3, "Lake",   1),
                        ("swampwater",FiresWorldGenBase.WC_SWAMP,     false, Minimap.PinType.Icon3, "SwampW", 1),
                        ("frozen",    FiresWorldGenBase.WC_FROZEN,    false, Minimap.PinType.Icon3, "Frozen", 1),
                        ("hotspring", FiresWorldGenBase.WC_HOTSPRING, false, Minimap.PinType.Icon0, "Spring", 1),
                        ("swampland", (byte)2,                        true,  Minimap.PinType.Icon2, "Swamp",  40),
                    };

                    void PinKind(string kk, byte id, bool biome, Minimap.PinType icon, string lbl, int min)
                    {
                        int found = biome
                            ? FiresWorldGenBase.FindBiomeRegions(id, pos, cells, min, 400)
                            : FiresWorldGenBase.FindWaterBodies(id, pos, cells, 400);
                        int shown = 0;
                        for (int i = 0; i < found; i++)
                        {
                            if (cells[i] < min) continue;
                            FiresMapMarks.Add(pos[i], icon, $"{lbl}{i + 1} ({cells[i] * 4}m2)");
                            shown++;
                        }
                        total += shown;
                        args.Context.AddString($"  {kk}: {shown}");
                    }

                    bool all = what == "all" || what == "list";
                    bool listOnly = what == "list";
                    bool any = false;
                    foreach (var kd in kinds)
                        if (all || what == kd.k)
                        {
                            any = true;
                            if (listOnly)
                            {
                                int found = kd.biome
                                    ? FiresWorldGenBase.FindBiomeRegions(kd.id, pos, cells, kd.min, 400)
                                    : FiresWorldGenBase.FindWaterBodies(kd.id, pos, cells, 400);
                                int c = 0; for (int i = 0; i < found; i++) if (cells[i] >= kd.min) c++;
                                args.Context.AddString($"  {kd.k}: {c}");
                            }
                            else PinKind(kd.k, kd.id, kd.biome, kd.icon, kd.lbl, kd.min);
                        }

                    if (all || what == "volcano")
                    {
                        var vb = new System.Collections.Generic.List<bool>();
                        int nv = FiresWorldGenBase.SnapshotVolcanos(pos, vb);
                        if (listOnly) args.Context.AddString($"  volcano: {nv}");
                        else { for (int i = 0; i < nv; i++) FiresMapMarks.Add(pos[i], Minimap.PinType.Icon1, $"Volcano{i + 1} {(vb[i] ? "brimming" : "pooled")}"); total += nv; args.Context.AddString($"  volcano: {nv}"); }
                        any = true;
                    }
                    if (all || what == "skyisland")
                    {
                        var rad = new System.Collections.Generic.List<float>();
                        var kind = new System.Collections.Generic.List<string>();
                        FiresSkyIslands.Snapshot(pos, rad, kind);
                        if (listOnly) args.Context.AddString($"  skyisland: {pos.Count}");
                        else { for (int i = 0; i < pos.Count; i++) FiresMapMarks.Add(pos[i], Minimap.PinType.Icon4, $"Isle{i + 1} {kind[i]}"); total += pos.Count; args.Context.AddString($"  skyisland: {pos.Count}"); }
                        any = true;
                    }

                    if (!any)
                        args.Context.AddString("va_features: unknown kind. Use: river stream pond lake swampwater swampland frozen hotspring volcano skyisland all list clear");
                    else if (listOnly)
                        args.Context.AddString("va_features: counts above ('va_features <kind>' or 'all' pins them).");
                    else
                        args.Context.AddString($"va_features: pinned {total} feature(s) ('va_features clear' removes them).");
                },
                isCheat: true);

            new Terminal.ConsoleCommand("va_water_seamscan",
                "[va_water_seamscan] Audit the GENERATED WATER field across zone borders: do adjacent zones agree on the shared cells they both compute? Reports wet/dry slits and level steps. (va_voxel_seamscan only covers terrain.)",
                args =>
                {
                    foreach (var line in VoxelWaterSurvey.SeamScan())
                    {
                        args.Context.AddString(line);
                        FiresLogger.LogInfo($"[FiresVoxelWater] {line}");
                    }
                });

            // Voxel water sheet material isolator — swaps every live sheet's
            // material and dumps the nearest sheet's full render chain, so an
            // invisible sheet names its own failing layer instead of us
            // guessing between shader, material, mesh and GameObject.
            new Terminal.ConsoleCommand("va_voxelwater_mat",
                "[va_voxelwater_mat 0|1|2|3] Sheet material: 3=Custom/FiresInlandWater, the ported inland shader (DEFAULT), 0=the ocean FiresLiquid material, 1=grid-off clone (diagnostic), 2=Standard blue (diagnostic). Re-skins live sheets and dumps the nearest sheet's render chain.",
                args =>
                {
                    int mode = 1;
                    if (args.Length > 1) int.TryParse(args[1], out mode);
                    VoxelChunkView.SetSheetMaterialMode(mode);
                    var p = Player.m_localPlayer;
                    string dump = p != null ? VoxelChunkView.DumpNearestSheet(p.transform.position) : "no player";
                    args.Context.AddString($"va_voxelwater_mat {mode} → {dump}");
                    FiresLogger.LogInfo($"[FiresVoxelWater] va_voxelwater_mat {mode} → {dump}");
                });

            new Terminal.ConsoleCommand("va_voxel_wrapall",
                "[va_voxel_wrapall] Burst-wrap every currently loaded zone to voxel terrain (VoxelWrap mode).",
                args =>
                {
                    int n = VoxelZoneStreamer.WrapAllNow();
                    args.Context.AddString($"va_voxel_wrapall → started {n} zone wrap(s).");
                }, isCheat: true);

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
                            FiresLogger.LogInfo($"[FiresVoxel] seamscan: chunk {c.Key} {(heal ? "HEALED" : "diverges")} {n2} border sample(s).");
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
                    FiresLogger.LogInfo($"[FiresVoxel] {msg}");
                });

            FiresLogger.LogInfo("[FiresVoxel] va_voxel_carve/fill/delete/cube/sphere registered.");
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
                FiresLogger.LogInfo($"[FiresVoxel] {cmd} center Y {center.y:F1} → {snapY:F1} " +
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
                FiresLogger.LogError($"[FiresVoxel] {cmd} threw: {ex}");
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

            var mode = VerdantsAscent.Terrain.FatInput.AltHeld ? VoxelBrushMode.Carve : VoxelBrushMode.Fill;
            try
            {
                string summary = VoxelWorld.ApplyBrush(center, VoxelBrushDrag.EffectiveGizmoRadius(), 0f, shape, mode);
                args.Context.AddString($"{cmd} → {summary}");
            }
            catch (System.Exception ex)
            {
                args.Context.AddString($"{cmd} FAILED: {ex.Message}");
                FiresLogger.LogError($"[FiresVoxel] {cmd} threw: {ex}");
            }
        }

        private static bool s_terminalHooked;

        // From FAT's one Terminal.InitTerminal hook (FatVanillaHooks).
        internal static void OnTerminalInit()
        {
            if (s_terminalHooked) return;
            s_terminalHooked = true;
            Register();
            VoxelSeamAudit.Register();
            WaterCensus.Register();
            FatZdoNear.Register();   // 0.2.782: read-only ZDO census by prefab + radius (who created each copy)
            Zones.FatLocationDedupe.Register();   // 0.2.788: thin locations generated twice (dry run unless 'apply')
            VoxelGenLiquid.Register();
            VoxelBrushDrag.EnsureAttached();
            VoxelCaveEnv.EnsureAttached();
        }
    }
}
