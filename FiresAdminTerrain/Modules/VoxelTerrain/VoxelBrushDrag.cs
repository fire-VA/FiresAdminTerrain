using UnityEngine;
using VerdantsAscent.Terrain.Lifecycle;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Terrain.Voxel
{
    // Round-88 — voxel brush DRAG + 3D GIZMO tools (WaterBrushDrag pattern).
    //
    // Tool families, gated by the selected piece name. There is no yaml and no
    // console round-trip: the pieces are ours (VoxelToolPieces) and this pump
    // owns every click.
    //   * FLAT RULER tools (Vcarve/Vfill/Vdelete): flattened-cylinder brushes,
    //     pumping constant-arc-length dabs while LMB is held.
    //   * 3D GIZMO tools (Vcube/Vsphere): a wireframe cube/sphere follows the
    //     aim point as a true 3D placement visual (cookie-style). Ctrl+Scroll
    //     resizes, LMB stamps + drags (3D spacing — strokes work up walls and
    //     through air, spheres dragged = smooth tubes), hold LeftAlt to CARVE
    //     instead of fill (gizmo turns red).
    //   * WATER SHAPING tools (Vriver/Vpour/Vpool): scroll = width, PgUp/PgDn =
    //     level offset. River and pour are strokes; Vpool is a CLICK tool whose
    //     next click connects to the last pool, with a hint ray between them.
    //     The world-changing work lives in WaterShapingTools.
    public sealed class VoxelBrushDrag : MonoBehaviour
    {
        private const float DRAG_FRESHNESS_SECONDS = 2f;
        private const float DAB_SPACING_RATIO = 0.3f;
        private const int MAX_DABS_PER_FRAME = 8;
        private const float LEVEL_OFFSET_STEP = 0.5f;
        private const float LEVEL_OFFSET_LIMIT = 64f;
        private const float REFUSAL_WARN_SECONDS = 10f;

        private static float _lastRefusalWarn = -100f;

        private static bool IsWaterTool(Tool tool)
            => tool == Tool.River || tool == Tool.Pour || tool == Tool.Pool;

        public static float GizmoSize = 4f;   // cube edge / sphere diameter (Ctrl+Scroll)

        private enum Tool { None, Cylinder, Cube, Sphere, River, Pour, Pool }

        // Cylinder-tool state, armed by the console command.
        private static VoxelBrushMode s_cylMode;
        private static float s_cylRadius;
        private static float s_cylSignedHeight;
        private static float s_lastCommandTime = -100f;

        public static void RecordCylinder(VoxelBrushMode mode, float radius, float signedHeight)
        {
            s_cylMode = mode;
            s_cylRadius = radius;
            s_cylSignedHeight = signedHeight;
            s_lastCommandTime = Time.realtimeSinceStartup;
        }

        // ── Virtual press (admintools `only tools`, FDT's `bot tool voxel`) ──────────────────────────────────────
        // A held LMB at a fixed cursor for a few seconds, fed into THIS Update: the same selected V-tool, admin gate, Core
        // place gate, dab pump and Dispatch a player's click runs. It acts only while the local player holds the Chaos Hammer
        // with a V-tool selected, so the bot's equip and pick are proven too (Fire, 2026-09-29: prove the tools in the bot's
        // hands). The end of every press logs one line FDT reads back:
        //   [FiresVoxel] virtual press: <tool> at (x,y,z) for <s> s -> <n> dab(s)
        private const string PressCommand = "va_voxel_press";
        private static float s_pressUntil;
        private static float s_pressSeconds;
        private static Vector3 s_pressCursor;
        private static bool s_pressAlt;
        private static bool s_pressing;
        private static int s_pressDabs;
        private static string s_pressTool;

        private static bool Pressing => s_pressing && Time.realtimeSinceStartup < s_pressUntil;

        // Null when the press started; else why not.
        public static string VirtualPress(Vector3 cursor, float seconds, bool alt, float size)
        {
            var player = Player.m_localPlayer;
            if (player == null) return "no local player";
            if (SelectedTool(player) == Tool.None) return "no V-tool selected: hold the Chaos Hammer and pick one";
            if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) return "not an admin";
            if (s_pressing) return "a press is already running";
            if (size > 0f)
            {
                VoxelToolPieces.ToolRadius = Mathf.Clamp(size, 0.5f, 32f);
                GizmoSize = VoxelToolPieces.ToolRadius * 2f;
            }
            s_pressTool = player.m_buildPieces.GetSelectedPiece().m_name;
            s_pressCursor = cursor;
            s_pressAlt = alt;
            s_pressSeconds = Mathf.Clamp(seconds, 0.1f, 10f);
            s_pressUntil = Time.realtimeSinceStartup + s_pressSeconds;
            s_pressDabs = 0;
            s_pressing = true;
            return null;
        }

        private void EndPressIfDue()
        {
            if (!s_pressing || Time.realtimeSinceStartup < s_pressUntil) return;
            s_pressing = false;
            _strokeActive = false;
            FiresLogger.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[FiresVoxel] virtual press: {0} at ({1:0.00},{2:0.00},{3:0.00}) for {4:0.0} s -> {5} dab(s)",
                s_pressTool, s_pressCursor.x, s_pressCursor.y, s_pressCursor.z, s_pressSeconds, s_pressDabs));
        }

        // `va_voxel_press <x> <y> <z> <seconds> [alt] [size]`: no devcommands needed; the V-tool's own admin gate decides.
        internal static void RegisterPressCommand()
        {
            new Terminal.ConsoleCommand(PressCommand,
                "<x> <y> <z> <seconds> [alt] [size] - hold the selected V-tool's LMB at a point (test bot)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var style = System.Globalization.NumberStyles.Float;
                    if (args.Args.Length < 5
                        || !float.TryParse(args.Args[1], style, inv, out float x) || !float.TryParse(args.Args[2], style, inv, out float y)
                        || !float.TryParse(args.Args[3], style, inv, out float z) || !float.TryParse(args.Args[4], style, inv, out float seconds))
                    {
                        args.Context.AddString($"{PressCommand} <x> <y> <z> <seconds> [alt] [size]");
                        return;
                    }
                    bool alt = false;
                    float size = 0f;
                    for (int i = 5; i < args.Args.Length; i++)
                    {
                        if (args.Args[i].Equals("alt", System.StringComparison.OrdinalIgnoreCase)) alt = true;
                        else float.TryParse(args.Args[i], style, inv, out size);
                    }
                    string refused = VirtualPress(new Vector3(x, y, z), seconds, alt, size);
                    if (refused != null) FiresLogger.LogWarning($"[FiresVoxel] virtual press refused: {refused}");
                    args.Context.AddString(refused == null ? $"{PressCommand}: pressing for {seconds:0.0} s" : $"{PressCommand} refused: {refused}");
                });
        }

        // ── Level a rectangular pad (FDT's obstacle_field levels its own site; Fire, 2026-09-29: "we could also just level a
        // spot and place the test") ──────────────────────────────────────────────────────────────────────────────────────
        // `va_voxel_level <x> <z> <sizeX> <sizeZ> <y|auto> [depth]`: every column of the rectangle ends at y. Ground under it is
        // filled up to y from no deeper than `depth` below (default 8, vanilla's level limit, so a cave under the pad stays
        // open) and ground above it is cut down to y, with the same V-tool brush a player's Vfill/Vcarve uses, stamped as
        // radius-4 cylinders on a 4 m grid (they reach up to 2 m past the rectangle's edge). `auto` = the median ground height
        // over the rectangle. Admin only, like the V-tools; no devcommands needed. The edit saves with its chunks' anchors
        // (0.75 s debounce) and every peer applies it from them. One line when done:
        //   [FiresVoxel] level pad at (x,z) SXxSZ m to y=Y: ground LO-HI, filled A-Y, cut Y-B, N stamp(s), skirt W m (M stamp(s))
        // The skirt (0.2.655): without it a pad on a slope was a walled pit on the cut side and a sheer drop on the fill side, a
        // trap for players, companions and the navmesh alike. Around the rectangle the ground now blends from y back to the
        // natural ground over W m (auto: wide enough for <= 30 deg at the edge's largest step, up to 20 m; 0 = no skirt),
        // stamped as radius-2 cylinders on a 2 m grid, cut where the ground stands above the blend and filled where below.
        private const string LevelCommand = "va_voxel_level";
        private const float LevelStampRadius = 4f, LevelStampSpacing = 4f, LevelDefaultDepth = 8f, LevelSurfaceReach = 24f;
        private const float SkirtStampRadius = 2f, SkirtStampSpacing = 2f, SkirtMaxWidth = 20f, SkirtMinStep = 0.3f;
        private const float PadWaterProbeStep = 4f, PadWaterMargin = 0.2f;
        private static readonly float SkirtSlope = Mathf.Tan(30f * Mathf.Deg2Rad);

        private static float LevelGround(WorldGenerator generator, float gx, float gz)
        {
            float gen = generator.GetHeight(gx, gz);
            return VoxelWorld.TryFindSurfaceY(gx, gz, gen, LevelSurfaceReach, out float surface) ? surface : gen;
        }

        internal static void RegisterLevelCommand()
        {
            new Terminal.ConsoleCommand(LevelCommand,
                "<x> <z> <sizeX> <sizeZ> <y|auto> [depth=8] [skirt=auto] - level a rectangular pad to y with the V-tool brush, sloped edges (admin)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var style = System.Globalization.NumberStyles.Float;
                    if (args.Args.Length < 6
                        || !float.TryParse(args.Args[1], style, inv, out float x) || !float.TryParse(args.Args[2], style, inv, out float z)
                        || !float.TryParse(args.Args[3], style, inv, out float sx) || !float.TryParse(args.Args[4], style, inv, out float sz))
                    {
                        args.Context.AddString($"{LevelCommand} <x> <z> <sizeX> <sizeZ> <y|auto> [depth]");
                        return;
                    }
                    float y = float.NaN;
                    if (!args.Args[5].Equals("auto", System.StringComparison.OrdinalIgnoreCase)
                        && !float.TryParse(args.Args[5], style, inv, out y))
                    {
                        args.Context.AddString($"{LevelCommand}: y must be a number or 'auto'");
                        return;
                    }
                    float depth = LevelDefaultDepth;
                    if (args.Args.Length > 6) float.TryParse(args.Args[6], style, inv, out depth);
                    float skirt = float.NaN;
                    if (args.Args.Length > 7 && !args.Args[7].Equals("auto", System.StringComparison.OrdinalIgnoreCase)
                        && !float.TryParse(args.Args[7], style, inv, out skirt))
                    {
                        args.Context.AddString($"{LevelCommand}: skirt must be a width in m, 0 (none) or 'auto'");
                        return;
                    }
                    string line;
                    // Anything outside the stamps (the ground survey, the skirt plan) logs its stack too, not only a message.
                    try { line = LevelPad(x, z, sx, sz, y, Mathf.Clamp(depth, 0.5f, 24f), skirt); }
                    catch (System.Exception ex)
                    {
                        line = $"[FiresVoxel] level pad threw before or after its stamps: {ex.GetType().Name}: {ex.Message}";
                        FiresLogger.LogError(line + "\n" + ex);
                    }
                    args.Context?.AddString(line);
                });
        }

        // The line logged (and returned); starts "refused:" when nothing was done.
        public static string LevelPad(float x, float z, float sizeX, float sizeZ, float y, float depth, float skirt = float.NaN)
        {
            if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin())
                return Refused("not an admin");
            if (sizeX < 1f || sizeZ < 1f || sizeX > 128f || sizeZ > 128f) return Refused("size must be 1-128 m each way");
            var generator = WorldGenerator.instance;
            if (generator == null) return Refused("no world generator");

            var heights = new System.Collections.Generic.List<float>();
            float lo = float.MaxValue, hi = float.MinValue;
            for (float dz = -sizeZ * 0.5f; dz <= sizeZ * 0.5f + 0.01f; dz += 1f)
                for (float dx = -sizeX * 0.5f; dx <= sizeX * 0.5f + 0.01f; dx += 1f)
                {
                    float h = LevelGround(generator, x + dx, z + dz);
                    heights.Add(h);
                    lo = Mathf.Min(lo, h);
                    hi = Mathf.Max(hi, h);
                }
            heights.Sort();
            if (float.IsNaN(y)) y = heights[heights.Count / 2];

            // The skirt is planned on the ground as it stands BEFORE any stamp (the pad's own stamps reach 2 m past the edge).
            float halfX = sizeX * 0.5f, halfZ = sizeZ * 0.5f;
            if (float.IsNaN(skirt))
            {
                float edgeStep = 0f;
                for (float dz = -halfZ - 1f; dz <= halfZ + 1.01f; dz += 2f)
                    for (float dx = -halfX - 1f; dx <= halfX + 1.01f; dx += 2f)
                        if (Mathf.Abs(dx) > halfX || Mathf.Abs(dz) > halfZ)
                            edgeStep = Mathf.Max(edgeStep, Mathf.Abs(LevelGround(generator, x + dx, z + dz) - y));
                // + one stamp radius: overlapping stamps land each point at the blend of the stamp one radius nearer the pad
                skirt = edgeStep < SkirtMinStep ? 0f : Mathf.Min(SkirtMaxWidth, edgeStep / SkirtSlope + SkirtStampRadius);
            }
            skirt = Mathf.Clamp(skirt, 0f, SkirtMaxWidth);

            // 0.2.679 (H7, 10-01: a 76x76 pad levelled to y 75.90 beside a hydrology pond at 77.8 flooded the whole yard).
            // A cut below standing water that it borders becomes that water's bed: the voxel water store spreads generated
            // water into every dug pocket it touches, as a real lake would. So no pad is cut under water: any inland water
            // within the pad and its skirt whose surface stands above y is refused, naming the spot. Pass a y above it, or
            // move the pad.
            for (float dz = -halfZ - skirt; dz <= halfZ + skirt + 0.01f; dz += PadWaterProbeStep)
                for (float dx = -halfX - skirt; dx <= halfX + skirt + 0.01f; dx += PadWaterProbeStep)
                    if (VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x + dx, z + dz, 0.05f, false, out float surface, out _)
                        && surface > y + PadWaterMargin)
                        return Refused(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "the pad at y {0:0.00} would sit under water: inland water at ({1:0},{2:0}) stands at {3:0.00}. A cut below water it "
                            + "touches floods. Level to y >= {4:0.00}, or move the pad away from that water", y, x + dx, z + dz, surface, surface + 0.5f));
            var skirtStamps = new System.Collections.Generic.List<Vector4>();   // x, z, ground, blend target
            if (skirt > 0f)
                for (float cz = z - halfZ - skirt + SkirtStampSpacing * 0.5f; cz < z + halfZ + skirt; cz += SkirtStampSpacing)
                    for (float cx = x - halfX - skirt + SkirtStampSpacing * 0.5f; cx < x + halfX + skirt; cx += SkirtStampSpacing)
                    {
                        float ox = Mathf.Max(0f, Mathf.Abs(cx - x) - halfX), oz = Mathf.Max(0f, Mathf.Abs(cz - z) - halfZ);
                        float d = Mathf.Sqrt(ox * ox + oz * oz);
                        if (d <= 0f || d > skirt) continue;
                        float g = LevelGround(generator, cx, cz);
                        float target = Mathf.Lerp(y, g, d / skirt);
                        if (Mathf.Abs(g - target) > 0.05f) skirtStamps.Add(new Vector4(cx, cz, g, target));
                    }

            int stamps = 0;
            s_stampFailures = 0;
            s_firstStampFailure = null;
            float bottom = Mathf.Max(lo, y - depth), top = Mathf.Min(hi, y + depth) + 1f;
            bool fill = lo < y - 0.05f, cut = hi > y + 0.05f;
            for (float cz = z - sizeZ * 0.5f + LevelStampSpacing * 0.5f; cz < z + sizeZ * 0.5f + LevelStampSpacing * 0.5f; cz += LevelStampSpacing)
                for (float cx = x - sizeX * 0.5f + LevelStampSpacing * 0.5f; cx < x + sizeX * 0.5f + LevelStampSpacing * 0.5f; cx += LevelStampSpacing)
                {
                    if (fill)
                        LevelStamp(new Vector3(cx, (bottom + y) * 0.5f, cz), LevelStampRadius, y - bottom, VoxelBrushShape.Cylinder, VoxelBrushMode.Fill);
                    if (cut)
                        LevelStamp(new Vector3(cx, (y + top) * 0.5f, cz), LevelStampRadius, top - y, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
                    stamps++;
                }
            foreach (var s in skirtStamps)
            {
                float g = s.z, target = s.w;
                if (g > target)
                    LevelStamp(new Vector3(s.x, (target + g + 1f) * 0.5f, s.y), SkirtStampRadius, g + 1f - target, VoxelBrushShape.Cylinder, VoxelBrushMode.Carve);
                else
                {
                    float from = Mathf.Max(g, target - depth);
                    LevelStamp(new Vector3(s.x, (from + target) * 0.5f, s.y), SkirtStampRadius, target - from, VoxelBrushShape.Cylinder, VoxelBrushMode.Fill);
                }
            }
            string done = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[FiresVoxel] level pad at ({0:0.0},{1:0.0}) {2:0}x{3:0} m to y={4:0.00}: ground {5:0.00}-{6:0.00}, {7}, {8}, {9} stamp(s), {10}",
                x, z, sizeX, sizeZ, y, lo, hi,
                fill ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "filled {0:0.00}-{1:0.00}", bottom, y) : "no fill",
                cut ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "cut {0:0.00}-{1:0.00}", y, top) : "no cut", stamps,
                skirt > 0f ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "skirt {0:0.0} m ({1} stamp(s))", skirt, skirtStamps.Count) : "no skirt");
            if (s_stampFailures > 0)
            {
                done += $"; {s_stampFailures} stamp(s) THREW, the pad is incomplete (first: {s_firstStampFailure})";
                FiresLogger.LogWarning(done);
                return done;
            }
            FiresLogger.LogInfo(done);
            return done;
        }

        // 0.2.676 (H4, 10-01: "va_voxel_level -232.9 230.0 76 76 auto threw Object reference not set…", and FDT kept no
        // stack): one stamp that throws no longer aborts the pad. The first failure is logged WITH its stack, stamp and
        // chunk key, the other stamps still run, and the result line counts the failures, so a partial pad is visible.
        private static int s_stampFailures;
        private static string s_firstStampFailure;

        private static void LevelStamp(Vector3 c, float radius, float height, VoxelBrushShape shape, VoxelBrushMode mode)
        {
            try { VoxelWorld.ApplyBrush(c, radius, height, shape, mode); }
            catch (System.Exception ex)
            {
                s_stampFailures++;
                if (s_firstStampFailure != null) return;
                var key = new Vector3Int(Mathf.FloorToInt(c.x / 32f), Mathf.FloorToInt(c.y / 32f), Mathf.FloorToInt(c.z / 32f));
                s_firstStampFailure = $"{mode} r={radius:0.0} at ({c.x:0.0},{c.y:0.0},{c.z:0.0}) chunk {key}: {ex.GetType().Name}: {ex.Message}";
                FiresLogger.LogError($"[FiresVoxel] level pad stamp threw: {s_firstStampFailure}\n{ex}");
            }
        }

        private static string Refused(string why)
        {
            string line = $"[FiresVoxel] level pad refused: {why}";
            FiresLogger.LogWarning(line);
            return line;
        }

        private Vector3 _lastDabPos;
        private bool _strokeActive;
        private float _dabBudget;
        private Tool _prevTool = Tool.None;
        private bool _armAfterRelease;   // a fresh tool selection must not double as a carve click

        private GameObject _gizmoCube;
        private GameObject _gizmoSphere;

        private void Update()
        {
            WaterShapingTools.Tick();
            EndPressIfDue();
            bool virtualPress = Pressing;

            var player = Player.m_localPlayer;
            Tool tool = player != null ? SelectedTool(player) : Tool.None;
            // Just selected/switched a V-tool (from the build menu) → require the
            // mouse to be released before the first carve, so the click that
            // picked the tool doesn't also dig. Cleared on the next LMB-up below.
            if (tool != Tool.None && tool != _prevTool) _armAfterRelease = true;
            // Leaving Vpool ends the chain. Without this, a pool placed minutes
            // later would connect itself to a stale anchor across the map and
            // carve a channel the whole way there.
            if (_prevTool == Tool.Pool && tool != Tool.Pool) WaterShapingTools.ClearAnchor();
            _prevTool = tool;

            // The build menu already hides these from non-admins, but menu
            // visibility is not enforcement — a piece can still be reached by
            // other means, and these strokes persist to everyone. Refuse at the
            // point of action too.
            bool refused = tool != Tool.None && !FiresCore.Sync.AdminSyncing.IsLocalAdmin();
            if (refused && Time.realtimeSinceStartup - _lastRefusalWarn > REFUSAL_WARN_SECONDS)
            {
                _lastRefusalWarn = Time.realtimeSinceStartup;
                FiresLogger.LogWarning(
                    "[FiresVoxel] a terrain/water tool was selected by a non-admin — all strokes refused.");
            }

            if (tool == Tool.None || refused
                || (FiresVoxelConfig.Enabled != null && !FiresVoxelConfig.Enabled.Value))
            {
                SetGizmo(Tool.None, Vector3.zero, false);
                UpdateHintRay(Tool.None, Vector3.zero);
                SetRulerVisualHidden(false);
                _strokeActive = false;
                return;
            }

            // THE IH DIVORCE — our own cursor (crosshair raycast) and our own
            // scroll radius. No ruler, no console arming.
            Vector3 cursor;
            if (virtualPress) cursor = s_pressCursor;
            else if (!TileBrush.TryGetPointerWorldPosition(out cursor))
            {
                SetGizmo(Tool.None, Vector3.zero, false);
                return;
            }

            bool carveMod = Input.GetKey(KeyCode.LeftAlt) || (virtualPress && s_pressAlt);
            SetRulerVisualHidden(true);   // IH's white ring never draws under our ghosts

            // Plain scroll = UNIFORM size (camera zoom is suppressed by the
            // VoxelToolPieces ZInput patch while a V-tool is selected): the
            // whole shape scales — radius AND cylinder height keep their
            // aspect. PgUp/PgDn changes the aspect (explicit height).
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f
                && (UnityEngine.EventSystems.EventSystem.current == null
                    || !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()))
            {
                float factor = 1f + scroll * 1.4f;
                VoxelToolPieces.ToolRadius = Mathf.Clamp(VoxelToolPieces.ToolRadius * factor, 0.5f, 32f);
                if (Mathf.Abs(VoxelToolPieces.CylHeight) > 0.01f)
                    VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight * factor, -24f, 24f);
            }
            if (tool == Tool.Cylinder)
            {
                if (Input.GetKeyDown(KeyCode.PageUp)) VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight + 1f, -24f, 24f);
                if (Input.GetKeyDown(KeyCode.PageDown)) VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight - 1f, -24f, 24f);
            }
            else if (IsWaterTool(tool))
            {
                // Same direction for all three: PgUp raises, PgDn lowers. River
                // reads it as bed depth, pour/pool as the surface level.
                float before = WaterShapingTools.LevelOffset;
                if (Input.GetKeyDown(KeyCode.PageUp))
                    WaterShapingTools.LevelOffset = Mathf.Clamp(before + LEVEL_OFFSET_STEP, -LEVEL_OFFSET_LIMIT, LEVEL_OFFSET_LIMIT);
                if (Input.GetKeyDown(KeyCode.PageDown))
                    WaterShapingTools.LevelOffset = Mathf.Clamp(before - LEVEL_OFFSET_STEP, -LEVEL_OFFSET_LIMIT, LEVEL_OFFSET_LIMIT);
                if (!Mathf.Approximately(before, WaterShapingTools.LevelOffset) && player != null)
                    player.Message(MessageHud.MessageType.TopLeft,
                        $"Level offset {WaterShapingTools.LevelOffset:+0.0;-0.0;0}m", 0, null, false);
            }

            if (tool == Tool.Cube || tool == Tool.Sphere)
            {
                SetGizmo(tool, cursor, carveMod);
                UpdateHintRay(tool, cursor);
            }
            else if (IsWaterTool(tool))
            {
                // The ghost sits at the level the tool will actually work at, so
                // the offset is visible BEFORE committing rather than after.
                // River digs (red ghost); pour and pool lay surface (green).
                var ghost = cursor;
                if (tool != Tool.River) ghost.y += WaterShapingTools.LevelOffset;
                s_cylRadius = VoxelToolPieces.ToolRadius;
                s_cylSignedHeight = 0f;
                SetGizmo(Tool.Cylinder, ghost, tool == Tool.River);
                UpdateHintRay(tool, ghost);
            }
            else
            {
                UpdateHintRay(tool, cursor);
                // Vcarve/Vdelete = dig ghost (lines only), Vfill = fill ghost
                // (terrain-shader solid). Always armed — no console freshness.
                SetGizmo(Tool.Cylinder, cursor, !SelectedIsFill(player));
                s_cylRadius = VoxelToolPieces.ToolRadius;
                s_cylSignedHeight = VoxelToolPieces.CylHeight;
                s_cylMode = SelectedCylMode(player);
            }

            // A virtual press has no real button to release after the pick: it counts as a fresh press.
            if (virtualPress && !Input.GetMouseButton(0)) _armAfterRelease = false;
            if (!Input.GetMouseButton(0) && !virtualPress)
            {
                _strokeActive = false;
                _armAfterRelease = false;   // selecting-click released — a fresh press now carves
                return;
            }
            // Still holding the button that selected the tool: show the ghost but
            // don't carve until it's released and pressed again. The local flag
            // only catches a tool CHANGE; the Core gate also covers re-picking
            // the tool you already had and the click that closes the hammer menu
            // (both of which used to carve on the spot).
            if (_armAfterRelease) return;
            if (!FiresCore.Input.FiresPlaceGate.ReadyToAct()) return;

            float dabSize =
                tool == Tool.Cylinder ? s_cylRadius :
                IsWaterTool(tool) ? VoxelToolPieces.ToolRadius : EffectiveGizmoRadius();
            if (dabSize < 0.05f) return;
            float spacing = Mathf.Max(dabSize * DAB_SPACING_RATIO, 0.25f);

            if (!_strokeActive)
            {
                // IH-free: WE place the first dab on click-down (there is no
                // console command doing it anymore), then the pump spaces the
                // rest of the stroke.
                Dispatch(tool, cursor, carveMod);
                _lastDabPos = cursor;
                _strokeActive = true;
                return;
            }

            // Vpool is a CLICK tool, not a stroke: one pool per press, and the
            // next press connects to it. Dragging must never chain a pool every
            // few metres, which is what the dab pump below would do.
            if (tool == Tool.Pool) return;

            _dabBudget = Mathf.Min(_dabBudget + TileBrushModes.DrawSpeed * MAX_DABS_PER_FRAME, MAX_DABS_PER_FRAME);
            while (_dabBudget >= 1f)
            {
                Vector3 delta = cursor - _lastDabPos;   // FULL 3D spacing — strokes climb walls
                float len = delta.magnitude;
                if (len < spacing) break;
                Vector3 dab = _lastDabPos + delta * (spacing / len);
                Dispatch(tool, dab, carveMod);
                _lastDabPos = dab;
                _dabBudget -= 1f;
            }
        }

        private void Dispatch(Tool tool, Vector3 pos, bool carveMod)
        {
            if (s_pressing) s_pressDabs++;
            DispatchNow(tool, pos, carveMod);
        }

        private void DispatchNow(Tool tool, Vector3 pos, bool carveMod)
        {
            try
            {
                switch (tool)
                {
                    case Tool.Cylinder:
                    {
                        float h = Mathf.Abs(s_cylSignedHeight);
                        if (h > 0.01f)
                        {
                            // Explicit height (PgUp/PgDn) = cylinder mode:
                            // trenches and shafts, offset toward the sign.
                            Vector3 c = pos;
                            c.y += s_cylSignedHeight * 0.5f;
                            VoxelWorld.ApplyBrush(c, s_cylRadius, h, VoxelBrushShape.Cylinder, s_cylMode);
                        }
                        else
                        {
                            // Default = TRUE SPHERE: the volume shown by the
                            // ghost is exactly the volume removed/placed —
                            // no thin-disc surprise, uniform by definition.
                            VoxelWorld.ApplyBrush(pos, s_cylRadius, 0f, VoxelBrushShape.Sphere, s_cylMode);
                        }
                        break;
                    }
                    case Tool.Cube:
                        VoxelWorld.ApplyBrush(pos, EffectiveGizmoRadius(), 0f, VoxelBrushShape.Cube,
                            carveMod ? VoxelBrushMode.Carve : VoxelBrushMode.Fill);
                        break;
                    case Tool.Sphere:
                        VoxelWorld.ApplyBrush(pos, EffectiveGizmoRadius(), 0f, VoxelBrushShape.Sphere,
                            carveMod ? VoxelBrushMode.Carve : VoxelBrushMode.Fill);
                        break;
                    case Tool.River:
                        WaterShapingTools.RiverDab(pos, VoxelToolPieces.ToolRadius);
                        break;
                    case Tool.Pour:
                        WaterShapingTools.PourDab(pos, VoxelToolPieces.ToolRadius);
                        break;
                    case Tool.Pool:
                        WaterShapingTools.PoolClick(pos, VoxelToolPieces.ToolRadius);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresVoxel] drag dispatch threw: {ex.Message}");
            }
        }

        // The IH ruler's white segmented ring reads as a second, wrong tool
        // visual under our ghosts. Hide its RENDER parts while a V-tool is
        // selected; the projector transform + radius keep updating (scroll
        // still resizes, TryGetRulerPosition still answers). Restore on
        // deselect.
        private static GameObject s_hiddenRuler;
        private static void SetRulerVisualHidden(bool hidden)
        {
            if (!hidden)
            {
                if (s_hiddenRuler != null)
                {
                    foreach (var cp2 in s_hiddenRuler.GetComponentsInChildren<CircleProjector>(true)) cp2.enabled = true;
                    foreach (var r in s_hiddenRuler.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                    s_hiddenRuler = null;
                }
                return;
            }
            if (!InfinityHammerRulerProbe.TryGetProjector(out var proj) || proj == null) return;
            if (s_hiddenRuler == proj)
            {
                // segments respawn each frame while the projector runs — keep them dark
                foreach (var r in proj.GetComponentsInChildren<Renderer>(true))
                    if (r.enabled) r.enabled = false;
                return;
            }
            SetRulerVisualHidden(false);
            s_hiddenRuler = proj;
            foreach (var cp in proj.GetComponentsInChildren<CircleProjector>(true)) cp.enabled = false;
            foreach (var r in proj.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        }

        // Vpool's A->B hint: the line from the pool you last placed to where the
        // next one would go, so the run can be judged before committing to it.
        private GameObject _hintRay;
        private LineRenderer _hintLine;

        private void UpdateHintRay(Tool tool, Vector3 cursor)
        {
            Vector3 from = Vector3.zero;
            bool want = tool == Tool.Pool && WaterShapingTools.TryGetAnchor(out from, out _);
            if (!want)
            {
                if (_hintRay != null) _hintRay.SetActive(false);
                return;
            }

            if (_hintRay == null)
            {
                _hintRay = new GameObject("fires_pool_hint");
                _hintLine = NewLine(_hintRay, 2);
            }
            _hintRay.SetActive(true);
            _hintLine.SetPosition(0, from);
            _hintLine.SetPosition(1, cursor);
        }

        private static bool SelectedIsFill(Player player)
        {
            var piece = player?.m_buildPieces?.GetSelectedPiece();
            return piece != null && piece.m_name == "Vfill";
        }

        private static VoxelBrushMode SelectedCylMode(Player player)
        {
            var piece = player?.m_buildPieces?.GetSelectedPiece();
            switch (piece != null ? piece.m_name : "")
            {
                case "Vfill": return VoxelBrushMode.Fill;
                case "Vdelete": return VoxelBrushMode.Restore;
                default: return VoxelBrushMode.Carve;
            }
        }

        // ── Tool gating by selected hoe piece name (YAML `name:` fields) ────
        private static Tool SelectedTool(Player player)
        {
            var pt = player.m_buildPieces;
            if (pt == null) return Tool.None;
            var piece = pt.GetSelectedPiece();
            if (piece == null) return Tool.None;
            switch (piece.m_name)
            {
                case "Vcarve":
                case "Vfill":
                case "Vdelete":
                    return Tool.Cylinder;
                case "Vcube":
                    return Tool.Cube;
                case "Vsphere":
                    return Tool.Sphere;
                case "Vriver":
                    return Tool.River;
                case "Vpour":
                    return Tool.Pour;
                case "Vpool":
                    return Tool.Pool;
                default:
                    return Tool.None;
            }
        }

        // ── 3D gizmo — the ruler's segmented green line, as a 3D shape ──────
        // The cube/sphere IS the ruler visual in 3D: same green, dashed
        // segments, and it scales live with the hoe ruler's radius. Ctrl+Scroll
        // sizing remains as the fallback when no ruler is active.
        private static readonly Color GIZMO_GREEN = new Color(0.30f, 0.95f, 0.35f, 1f);   // RulerColorDriver TERRAIN_GREEN
        private static readonly Color GIZMO_CARVE = new Color(1f, 0.25f, 0.2f, 1f);

        // IH-free: the ONE radius is ours (plain scroll while a V-tool is
        // selected). No projector probe, no ruler.
        internal static float EffectiveGizmoRadius()
            => Mathf.Clamp(VoxelToolPieces.ToolRadius, 0.5f, 32f);

        private void SetGizmo(Tool tool, Vector3 pos, bool carve)
        {
            if (tool != Tool.Cube && _gizmoCube != null) _gizmoCube.SetActive(false);
            if ((tool != Tool.Sphere && tool != Tool.Cylinder) && _gizmoSphere != null) _gizmoSphere.SetActive(false);
            if (tool == Tool.None) return;

            var go = tool == Tool.Cube ? EnsureCubeGizmo() : EnsureSphereGizmo();
            go.SetActive(true);
            go.transform.position = pos;
            if (tool == Tool.Cylinder)
            {
                // Ghost = THE brush volume. Default (no explicit height) is a
                // true sphere; PgUp/PgDn cylinder mode squashes to r × h.
                float r = s_cylRadius > 0.05f ? s_cylRadius : EffectiveGizmoRadius();
                float hAbs = Mathf.Abs(s_cylSignedHeight);
                go.transform.localScale = hAbs > 0.01f
                    ? new Vector3(r * 2f, hAbs, r * 2f)
                    : Vector3.one * (r * 2f);
            }
            else
            {
                go.transform.localScale = Vector3.one * (EffectiveGizmoRadius() * 2f);
            }
            // Slow ruler-style spin — cage only (the solid preview and the
            // cube's stamp volume stay world-aligned).
            var cage = go.transform.Find("cage");
            if (cage != null && tool != Tool.Cube)
                cage.localRotation = Quaternion.Euler(0f, Time.time * CAGE_SPIN_DEG_PER_SEC, 0f);
            // DIG = green dotted lines only; FILL = the same lines over a
            // solid preview wearing THE TERRAIN SHADER — you see exactly the
            // ground you are about to place. (User spec: no red state.)
            foreach (var lr in go.GetComponentsInChildren<LineRenderer>(true))
            {
                lr.startColor = GIZMO_GREEN;
                lr.endColor = GIZMO_GREEN;
            }
            var solid = go.transform.Find("solid");
            if (solid != null)
            {
                bool showSolid = !carve;
                if (solid.gameObject.activeSelf != showSolid) solid.gameObject.SetActive(showSolid);
                if (showSolid)
                {
                    var mr = solid.GetComponent<MeshRenderer>();
                    var mat = FiresCore.Materials.TerrainMaterialCache.GetCurrentMaterial();
                    if (mr != null && mat != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat;
                }
            }
        }

        // Solid preview child: a mesh copy of the Unity primitive (never
        // mutate the shared primitive mesh) with meadows vertex colors so the
        // terrain shader shows plain grass/rock instead of a biome mash.
        internal static void AddSolidChild(GameObject parent, PrimitiveType type)
        {
            var prim = GameObject.CreatePrimitive(type);
            var srcMesh = prim.GetComponent<MeshFilter>().sharedMesh;
            var mesh = Object.Instantiate(srcMesh);
            var cols = new Color32[mesh.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(0, 0, 0, 0);
            mesh.colors32 = cols;
            Object.Destroy(prim);

            var child = new GameObject("solid");
            child.transform.SetParent(parent.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = child.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static Texture2D _dashTex;
        private static Texture2D DashTex()
        {
            if (_dashTex != null) return _dashTex;
            _dashTex = new Texture2D(8, 2, TextureFormat.RGBA32, false);
            var px = new Color32[16];
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 8; x++)
                    px[y * 8 + x] = x < 5 ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            _dashTex.SetPixels32(px);
            _dashTex.Apply(false, false);
            _dashTex.wrapMode = TextureWrapMode.Repeat;
            _dashTex.filterMode = FilterMode.Point;
            return _dashTex;
        }

        private static Material s_sharedLineMat;
        internal static Material SharedLineMat()
        {
            if (s_sharedLineMat != null) return s_sharedLineMat;
            var sh = Shader.Find("Sprites/Default");
            s_sharedLineMat = new Material(sh) { name = "FiresVoxelGizmo", mainTexture = DashTex() };
            s_sharedLineMat.mainTextureScale = new Vector2(1.2f, 1f);   // ≈ one dash per 0.8m of line
            return s_sharedLineMat;
        }

        private Material GizmoMat() => SharedLineMat();

        private LineRenderer NewLine(GameObject parent, int positions)
        {
            var child = new GameObject("line");
            child.transform.SetParent(parent.transform, false);
            var lr = child.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = false;
            lr.widthMultiplier = 0.08f;
            lr.material = GizmoMat();
            lr.textureMode = LineTextureMode.Tile;
            lr.positionCount = positions;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return lr;
        }

        private GameObject EnsureCubeGizmo()
        {
            if (_gizmoCube != null) return _gizmoCube;
            _gizmoCube = new GameObject("FiresVoxelGizmo_Cube");
            _gizmoCube.transform.SetParent(transform, false);
            AddSolidChild(_gizmoCube, PrimitiveType.Cube);
            var cubeCage = new GameObject("cage");
            cubeCage.transform.SetParent(_gizmoCube.transform, false);
            cubeCage.transform.localScale = Vector3.one * CAGE_INFLATE;
            // One 16-point strip walks all 12 edges of a unit cube.
            var lr = NewLine(cubeCage, 16);
            float h = 0.5f;
            var p = new[]
            {
                new Vector3(-h,-h,-h), new Vector3( h,-h,-h), new Vector3( h,-h, h), new Vector3(-h,-h, h),
                new Vector3(-h,-h,-h), new Vector3(-h, h,-h), new Vector3( h, h,-h), new Vector3( h,-h,-h),
                new Vector3( h, h,-h), new Vector3( h, h, h), new Vector3( h,-h, h),
                new Vector3( h, h, h), new Vector3(-h, h, h), new Vector3(-h,-h, h),
                new Vector3(-h, h, h), new Vector3(-h, h,-h),
            };
            lr.SetPositions(p);
            return _gizmoCube;
        }

        private GameObject EnsureSphereGizmo()
        {
            if (_gizmoSphere != null) return _gizmoSphere;
            _gizmoSphere = BuildSphereGizmo(transform, GizmoMat());
            AddSolidChild(_gizmoSphere, PrimitiveType.Sphere);
            return _gizmoSphere;
        }

        // Shared builder (water tools reuse it): three orthogonal great
        // circles + four extra meridians — the dotted-cage look from the
        // user's reference shot. Lines live under a "cage" child scaled 2%
        // proud of the unit volume (never z-fights the solid preview or the
        // freshly-stamped terrain) and the cage slow-rotates like the IH
        // ruler (driven from SetGizmo/UpdateGhost each frame).
        internal const float CAGE_INFLATE = 1.02f;
        internal const float CAGE_SPIN_DEG_PER_SEC = 12f;

        internal static GameObject BuildSphereGizmo(Transform parent, Material lineMat)
        {
            var go = new GameObject("FiresGizmo_Sphere");
            go.transform.SetParent(parent, false);
            var cage = new GameObject("cage");
            cage.transform.SetParent(go.transform, false);
            cage.transform.localScale = Vector3.one * CAGE_INFLATE;
            const int SEG = 32;
            LineRenderer Line()
            {
                var child = new GameObject("line");
                child.transform.SetParent(cage.transform, false);
                var lr = child.AddComponent<LineRenderer>();
                lr.useWorldSpace = false;
                lr.loop = false;
                lr.widthMultiplier = 0.08f;
                lr.material = lineMat;
                lr.textureMode = LineTextureMode.Tile;
                lr.positionCount = SEG + 1;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                return lr;
            }
            for (int axis = 0; axis < 3; axis++)
            {
                var lr = Line();
                for (int i = 0; i <= SEG; i++)
                {
                    float a = i / (float)SEG * Mathf.PI * 2f;
                    float c = Mathf.Cos(a) * 0.5f, s = Mathf.Sin(a) * 0.5f;
                    Vector3 v = axis == 0 ? new Vector3(0f, c, s)
                        : axis == 1 ? new Vector3(c, 0f, s)
                        : new Vector3(c, s, 0f);
                    lr.SetPosition(i, v);
                }
            }
            for (int m = 0; m < 4; m++)
            {
                float yaw = (m + 1) * Mathf.PI / 4f;    // meridians at 45° steps (0/90 covered above)
                if (m == 1) continue;                    // 90° duplicates the axis-0 circle
                var lr = Line();
                float cy = Mathf.Cos(yaw), sy = Mathf.Sin(yaw);
                for (int i = 0; i <= SEG; i++)
                {
                    float a = i / (float)SEG * Mathf.PI * 2f;
                    float c = Mathf.Cos(a) * 0.5f, s = Mathf.Sin(a) * 0.5f;
                    lr.SetPosition(i, new Vector3(c * cy, s, c * sy));
                }
            }
            return go;
        }

        // Attach to the plugin root (DontDestroyOnLoad) — WaterBrushDrag pattern.
        public static void EnsureAttached()
        {
            var root = FiresAdminTerrain.RootObject;
            if (root == null) return;
            if (root.GetComponent<VoxelBrushDrag>() != null) return;
            root.AddComponent<VoxelBrushDrag>();
            FiresLogger.LogInfo("[FiresVoxel] VoxelBrushDrag attached (drag + gizmo tools).");
        }
    }
}
