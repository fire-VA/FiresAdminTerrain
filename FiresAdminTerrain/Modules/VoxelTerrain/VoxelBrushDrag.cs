using System.Collections.Generic;
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
    //   * LEVEL tool (Vlevel): the first click's height is the stroke's target;
    //     every dab cuts the brush disc down to it and fills it up to it.
    //   * WATER SHAPING tools (Vriver/Vpour/Vpool): scroll = width, PgUp/PgDn =
    //     level offset. River and pour are strokes; Vpool is a CLICK tool whose
    //     next click connects to the last pool, with a hint ray between them.
    //     The world-changing work lives in WaterShapingTools.
    public sealed partial class VoxelBrushDrag : MonoBehaviour
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

        public static float GizmoSize = 4f;   // cube edge / sphere diameter (twice the wheel radius)

        private enum Tool { None, Cylinder, Cube, Sphere, River, Pour, Pool, Level, Fall, Mop, Pond, Skin }

        // Vlevel (a level tool like Infinity Hammer's, for the voxel terrain): the cursor
        // height of the stroke's FIRST dab is the target every later dab of that stroke levels to.
        private static float s_levelTarget;
        private const float LevelGhostThickness = 0.3f;
        private const float LevelFineStep = 0.1f, LevelRepeatDelay = 0.35f, LevelRepeatRate = 8f;
        private static float s_levelHeldSince;
        private static float s_levelOffset;   // Vlevel's height over the ground under the crosshair (PgUp/PgDn, Shift+wheel)
        private static float s_heightWheelNotches;   // this frame's Shift+wheel notches, for the tool's one height value

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
        // with a V-tool selected, so the bot's equip and pick are proven too (the tools are proven in the bot's
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

        // 0.2.1334 (Fire: a mask left the disks in place, so a redraw stacked levels): a disk wholly inside the ring is REMOVED through
        // the server path (WaterOperationRPC.RequestRemoveWater, by exact hash); this runs inside DispatchNow's undo scope, so
        // FatUndo's WaterRemove_Undo keeps the disk (Serialize carries Kind) in this stroke's one step: undo re-lays, redo removes.
        // A disk only partly inside stays, with the ring's mask over that part. Where no Full chunk holds the columns (heightmap
        // ground) the mask cannot land: a partial disk there stays WHOLE and wet, logged below. Generated water keeps the mask.
        private static readonly List<VerdantsAscent.Water.Core.WaterDisk> s_mopPartial = new List<VerdantsAscent.Water.Core.WaterDisk>();
        private static readonly HashSet<int> s_mopPartialMasked = new HashSet<int>(), s_mopPartialUnmasked = new HashSet<int>();
        private static int s_mopRemoved, s_mopColumns, s_mopDabs;
        private static float s_mopLastDab;
        private static bool s_mopInfoDone;

        private static void NoteMopDisks(Vector3 pos, float r, int cols)
        {
            if (s_mopDabs > 0 && Time.unscaledTime - s_mopLastDab > 0.5f) FlushMopStroke();
            s_mopPartial.Clear();
            s_mopRemoved += VerdantsAscent.Water.Core.WaterOperationRPC.RequestRemoveWaterWhollyInside(pos, r, s_mopPartial, y => VoxelWorld.MopReachesY(pos, r, y));
            foreach (var disk in s_mopPartial)
            {
                // The middle of the overlap, on the line between the two centres: masked there = the mask landed on this disk.
                Vector3 toDisk = disk.Position - pos; toDisk.y = 0f;
                float overlap = r - disk.GetEdgeDistance(pos);
                Vector3 at = pos + (toDisk.sqrMagnitude > 0.0001f ? toDisk.normalized * Mathf.Max(0f, r - overlap * 0.5f) : Vector3.zero);
                bool masked = VoxelWorld.IsMoppedAt(at.x, disk.Position.y, at.z);
                (masked ? s_mopPartialMasked : s_mopPartialUnmasked).Add(disk.StableHash);
            }
            foreach (int hash in s_mopPartialMasked) s_mopPartialUnmasked.Remove(hash);
            s_mopColumns += cols;
            s_mopDabs++;
            s_mopLastDab = Time.unscaledTime;
            if (!s_mopInfoDone && s_mopRemoved > 0)
            {
                s_mopInfoDone = true;
                FiresLogger.LogInfo("[Vmop] deletes placed disks wholly inside its ring (undo re-lays them); partly covered disks stay under the mask");
            }
        }

        private static void FlushMopStroke()
        {
            if (s_mopDabs == 0) return;
            FiresLogger.LogDebug($"[Vmop] stroke: {s_mopDabs} dab(s), disks removed {s_mopRemoved}, disks partial (masked) {s_mopPartialMasked.Count}, "
                + $"disks partial with NO mask (no voxel chunk here, they stay whole) {s_mopPartialUnmasked.Count}, columns masked {s_mopColumns}");
            s_mopRemoved = s_mopColumns = s_mopDabs = 0;
            s_mopPartialMasked.Clear();
            s_mopPartialUnmasked.Clear();
        }

        private void EndPressIfDue()
        {
            if (!s_pressing || Time.realtimeSinceStartup < s_pressUntil) return;
            s_pressing = false;
            _strokeActive = false;
            FlushMopStroke();
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

        // ── Level a rectangular pad (FDT's obstacle_field levels its own site: level a spot, then place the test)
        // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        // `va_voxel_level <x> <z> <sizeX> <sizeZ> <y|auto> [depth]`: every column of the rectangle ends at y. Ground under it is
        // filled up to y from no deeper than `depth` below (default 8, vanilla's level limit, so a cave under the pad stays
        // open) and ground above it is cut down to y, with the same V-tool brush a player's Vfill/Vcarve uses, stamped as
        // radius-4 cylinders on a 4 m grid (they reach up to 2 m past the rectangle's edge). `auto` = the median ground height
        // over the rectangle. Admin only, like the V-tools; no devcommands needed. The edit saves with its chunks' anchors
        // (0.75 s debounce) and every peer applies it from them. One line when done:
        //   [FiresVoxel] level pad at (x,z) SXxSZ m to y=Y: ground LO-HI, filled A-Y, cut Y-B, N stamp(s), skirt W m (M stamp(s))
        // The skirt: without it a pad on a slope was a walled pit on the cut side and a sheer drop on the fill side, a
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

        // The top surface of `layer` at (x, z), searched from [bottom, top] and WIDENED until found: while the column
        // is still solid at the window's top (the ground rises past it: a mountain shoulder) the top climbs LevelWindowStep at
        // a time, and while nothing is found the bottom sinks, both only as far as the layer and the world allow. This is what
        // let the level work on any slope: the old fixed window (the target +-LevelSurfaceReach) skipped every sample whose
        // ground stood above it, so the cut stopped at that height ("refusing to level over a certain point").
        private const float LevelWindowStep = 64f, LevelFootRing = 1.15f;

        private static bool SurfaceInLayer(in TerrainLayer layer, float x, float z, float top, float bottom, out float surface)
        {
            float ceiling = Mathf.Min(LevelWorldTop, layer.Ceiling + FiresTerrainLayers.RoofKeep - 0.25f);
            float floor = Mathf.Max(LevelWorldBottom, layer.Floor - 0.25f);
            top = Mathf.Min(top, ceiling);
            bottom = Mathf.Max(bottom, floor);
            while (true)
            {
                bool found = VoxelWorld.TrySampleSurfaceY(x, z, top, bottom, out surface, out bool solidAtTop);
                if (solidAtTop && top < ceiling) { top = Mathf.Min(ceiling, top + LevelWindowStep); continue; }
                if (!found && bottom > floor) { bottom = Mathf.Max(floor, bottom - LevelWindowStep); continue; }
                return found;
            }
        }

        // The lowest and highest ground of `layer` under a disc (centre + a ring of 8 at 0.95 r), searched around both the
        // ground the generator reports there and the pad's target.
        private static bool SurveyDisc(in TerrainLayer layer, float x, float z, float radius, float near, float target, out float lo, out float hi)
        {
            lo = float.MaxValue; hi = float.MinValue;
            float top = Mathf.Max(near, target) + LevelSurfaceReach, bottom = Mathf.Min(near, target) - LevelSurfaceReach;
            for (int i = -1; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f, r = i < 0 ? 0f : radius * 0.95f;
                if (!SurfaceInLayer(layer, x + Mathf.Cos(a) * r, z + Mathf.Sin(a) * r, top, bottom, out float h)) continue;
                lo = Mathf.Min(lo, h);
                hi = Mathf.Max(hi, h);
            }
            return lo <= hi;
        }

        // One column of the pad, [bottom, top] clipped to the layer, stamped in LevelSegment-tall cylinders (0.5 m overlap).
        private static void StampSpan(in TerrainLayer layer, float x, float z, float radius, float bottom, float top, VoxelBrushMode mode)
        {
            bottom = Mathf.Max(bottom, LevelWorldBottom);
            top = Mathf.Min(top, LevelWorldTop);
            if (!layer.ClampSpan(ref bottom, ref top) || top <= bottom + 0.05f) return;
            for (float y0 = bottom; y0 < top - 0.01f; y0 += LevelSegment)
            {
                float y1 = Mathf.Min(top, y0 + LevelSegment + 0.5f);
                LevelStamp(new Vector3(x, (y0 + y1) * 0.5f, z), radius, y1 - y0, VoxelBrushShape.Cylinder, mode);
            }
        }

        // `fat_level_verify <x> <z> <sizeX> <sizeZ> <y>` (read-only), the easy check for a levelled pad. TOP: every
        // 1 m point of the rectangle has its top surface within LevelVerifyTolerance of y (the spread is printed). WALLS: every
        // 1 m column along the rectangle's edge (0.5 m inside it) is solid from just under y down to the ground: a column that
        // goes solid -> air -> solid on the way down is an OPEN gap under the pad, one that starts in air is MISSING. Columns
        // in unloaded chunks are counted apart. One Info line, PASS or FAIL.
        private const float LevelVerifyTolerance = 0.3f, LevelVerifyDepth = 300f, LevelVerifyStep = 0.5f;

        // FDT's Auto Census runs its whole command list in ONE frame, so a verify listed
        // after va_voxel_level would read the ground before the level's stamps had applied: an optional [delaySeconds] runs the
        // check that many seconds later (from Update), so `va_voxel_level …, fat_level_verify … 10` works in one census.
        private struct PendingVerify { public float Due, X, Z, SizeX, SizeZ, Y; }
        private static readonly System.Collections.Generic.List<PendingVerify> s_pendingVerifies = new System.Collections.Generic.List<PendingVerify>();

        // `fat_goto <x> <y> <z>` lands at that EXACT height, a cave floor included. Vanilla's teleport raises the
        // arrival to the top solid height (so a goto into a cave lands on the hill above it); this one teleports, then, once
        // the teleport has finished, puts the player at the asked point and lets them fall to the floor under it.
        private static Vector3? s_gotoPin;
        private static float s_gotoGiveUp;

        private static void TickTestCommands()
        {
            for (int i = s_pendingVerifies.Count - 1; i >= 0; i--)
            {
                var v = s_pendingVerifies[i];
                if (Time.unscaledTime < v.Due) continue;
                s_pendingVerifies.RemoveAt(i);
                FiresLogger.LogInfo(VerifyPad(v.X, v.Z, v.SizeX, v.SizeZ, v.Y));
            }
            if (s_gotoPin.HasValue)
            {
                var player = Player.m_localPlayer;
                if (player == null || Time.unscaledTime > s_gotoGiveUp)
                {
                    FiresLogger.LogWarning("[FiresVoxel] fat_goto: gave up (no player, or the teleport never finished in 60 s)");
                    s_gotoPin = null;
                    return;
                }
                if (player.IsTeleporting()) return;
                var at = s_gotoPin.Value;
                s_gotoPin = null;
                player.transform.position = at;
                var body = player.GetComponent<Rigidbody>();
                if (body != null) { body.position = at; body.linearVelocity = Vector3.zero; }
                FiresLogger.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[FiresVoxel] fat_goto: placed at ({0:0.0},{1:0.0},{2:0.0}) after the teleport", at.x, at.y, at.z));
            }
        }

        private static void RegisterGoto()
        {
            new Terminal.ConsoleCommand("fat_goto",
                "<x> <y> <z> - teleport to that exact point, a cave floor included (vanilla goto lands on the top surface) (admin)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var style = System.Globalization.NumberStyles.Float;
                    var player = Player.m_localPlayer;
                    if (args.Args.Length < 4 || player == null
                        || !float.TryParse(args.Args[1], style, inv, out float x) || !float.TryParse(args.Args[2], style, inv, out float y)
                        || !float.TryParse(args.Args[3], style, inv, out float z))
                    {
                        args.Context?.AddString("fat_goto <x> <y> <z>");
                        return;
                    }
                    if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) { args.Context?.AddString("fat_goto: not an admin"); return; }
                    var at = new Vector3(x, y, z);
                    player.TeleportTo(at, player.transform.rotation, true);
                    s_gotoPin = at;
                    s_gotoGiveUp = Time.unscaledTime + 60f;
                    string line = string.Format(inv, "[FiresVoxel] fat_goto: teleporting to ({0:0.0},{1:0.0},{2:0.0})", x, y, z);
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                });
        }

        private static void RegisterLevelVerify()
        {
            RegisterGoto();
            new Terminal.ConsoleCommand("fat_level_verify",
                "<x> <z> <sizeX> <sizeZ> <y> [delaySeconds] - check a levelled pad: top within 0.3 m of y everywhere, edge columns solid down to the ground (read-only)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var style = System.Globalization.NumberStyles.Float;
                    if (args.Args.Length < 6
                        || !float.TryParse(args.Args[1], style, inv, out float x) || !float.TryParse(args.Args[2], style, inv, out float z)
                        || !float.TryParse(args.Args[3], style, inv, out float sx) || !float.TryParse(args.Args[4], style, inv, out float sz)
                        || !float.TryParse(args.Args[5], style, inv, out float y))
                    {
                        args.Context?.AddString("fat_level_verify <x> <z> <sizeX> <sizeZ> <y>");
                        return;
                    }
                    if (args.Args.Length > 6 && float.TryParse(args.Args[6], style, inv, out float delay) && delay > 0f)
                    {
                        s_pendingVerifies.Add(new PendingVerify { Due = Time.unscaledTime + delay, X = x, Z = z, SizeX = sx, SizeZ = sz, Y = y });
                        args.Context?.AddString(string.Format(inv, "fat_level_verify: runs in {0:0} s", delay));
                        return;
                    }
                    string line = VerifyPad(x, z, sx, sz, y);
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                });
        }

        internal static string VerifyPad(float x, float z, float sx, float sz, float y)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float hx = sx * 0.5f, hz = sz * 0.5f;
            int top = 0, off = 0, noTop = 0;
            float spread = 0f;
            string worstTop = null;
            for (float dz = -hz + 0.5f; dz <= hz - 0.5f + 0.01f; dz += 1f)
                for (float dx = -hx + 0.5f; dx <= hx - 0.5f + 0.01f; dx += 1f)
                {
                    top++;
                    if (!VoxelWorld.TrySampleSurfaceY(x + dx, z + dz, y + 30f, y - 30f, out float h, out _)) { noTop++; continue; }
                    float e = Mathf.Abs(h - y);
                    if (e > spread) { spread = e; worstTop = string.Format(inv, "({0:0.0},{1:0.0}) at {2:0.00}", x + dx, z + dz, h); }
                    if (e > LevelVerifyTolerance) off++;
                }
            int walls = 0, open = 0, missing = 0, unloaded = 0;
            string firstOpen = null;
            var edge = new System.Collections.Generic.List<Vector2>();
            for (float d = -hx + 0.5f; d <= hx - 0.5f + 0.01f; d += 1f) { edge.Add(new Vector2(x + d, z - hz + 0.5f)); edge.Add(new Vector2(x + d, z + hz - 0.5f)); }
            for (float d = -hz + 1.5f; d <= hz - 1.5f + 0.01f; d += 1f) { edge.Add(new Vector2(x - hx + 0.5f, z + d)); edge.Add(new Vector2(x + hx - 0.5f, z + d)); }
            foreach (var p in edge)
            {
                walls++;
                // Down from just under the pad's top: the pad's solid, then (if open) air, then the ground's solid again.
                int phase = 0;   // 0 = in the pad, 1 = in a gap under it
                bool known = true, startedSolid = false;
                for (float wy = y - 0.75f; wy > y - LevelVerifyDepth; wy -= LevelVerifyStep)
                {
                    if (!VoxelWorld.TryIsSolid(p.x, wy, p.y, out bool solid)) { known = false; break; }
                    if (wy == y - 0.75f) { startedSolid = solid; if (!solid) break; }
                    if (phase == 0 && !solid) phase = 1;
                    else if (phase == 1 && solid)
                    {
                        open++;
                        firstOpen ??= string.Format(inv, "({0:0.0},{1:0.0}) air below y {2:0.0}", p.x, p.y, wy);
                        break;
                    }
                }
                if (!known) { unloaded++; continue; }
                if (!startedSolid) missing++;
            }
            bool pass = off == 0 && noTop == 0 && open == 0 && missing == 0 && unloaded == 0;
            return string.Format(inv,
                "[FiresVoxel] level verify ({0:0.0},{1:0.0}) {2:0}x{3:0} m y={4:0.00}: top {5} point(s), spread {6:0.00} m{7}, {8} off by > {9:0.0} m, {10} with no surface; "
                + "edge {11} column(s): {12} OPEN under the pad{13}, {14} missing at the top, {15} unloaded - {16}",
                x, z, sx, sz, y, top, spread, worstTop != null ? " (worst " + worstTop + ")" : "", off, LevelVerifyTolerance, noTop,
                walls, open, firstOpen != null ? " (first " + firstOpen + ")" : "", missing, unloaded, pass ? "PASS" : "FAIL");
        }

        internal static void RegisterLevelCommand()
        {
            RegisterLevelVerify();
            VoxelPaintCommand.Register();   // va_voxel_paint (FDT's pads)
            FatTerrainSnapshot.RegisterCommand();   // fat_snapshot_test (camp terrain snapshots)
            VerdantsAscent.Terrain.Testing.FatBlackPaintTest.Register();   // 0.2.1378
            VerdantsAscent.Terrain.HeightmapSnow.FatTrailFieldTest.Register();   // 0.2.1288 fat_trail_field_test
            VerdantsAscent.Terrain.HeightmapSnow.FatZonePaintDump.Register();   // 0.2.1306 fat_zone_paint_dump
            FatTerrainSnapshot.RegisterResidueCommand(); FatTerrainSnapshot.RegisterPreCampCheck(); FiresWorldGenBase.RegisterPackAbCommand(); FatStudioRoundtrip.Register(); global::VerdantsAscent.Terrain.Studio.FatStudioTerrain.RegisterCommand(); global::VerdantsAscent.Terrain.Studio.FatStudioLocations.RegisterCommand(); global::VerdantsAscent.Terrain.Studio.FatStudioWaterTest.RegisterCommand();   // fat_camp_residue_clear, fat_studio_terrain_test (0.2.1242)
            new Terminal.ConsoleCommand(LevelCommand,
                "<x> <z> <sizeX> <sizeZ> <y|auto> [depth: ignored since 0.2.807] [skirt=auto] - level a rectangular pad to y with the V-tool brush, sloped edges, any height (admin)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var style = System.Globalization.NumberStyles.Float;
                    // '--force' (a manual admin only) levels even over player-built pieces.
                    var A = System.Array.FindAll(args.Args, s => !s.Equals("--force", System.StringComparison.OrdinalIgnoreCase));
                    bool force = A.Length != args.Args.Length;
                    if (A.Length < 6
                        || !float.TryParse(A[1], style, inv, out float x) || !float.TryParse(A[2], style, inv, out float z)
                        || !float.TryParse(A[3], style, inv, out float sx) || !float.TryParse(A[4], style, inv, out float sz))
                    {
                        args.Context.AddString($"{LevelCommand} <x> <z> <sizeX> <sizeZ> <y|auto> [depth]");
                        return;
                    }
                    float y = float.NaN;
                    if (!A[5].Equals("auto", System.StringComparison.OrdinalIgnoreCase)
                        && !float.TryParse(A[5], style, inv, out y))
                    {
                        args.Context.AddString($"{LevelCommand}: y must be a number or 'auto'");
                        return;
                    }
                    float depth = LevelDefaultDepth;
                    if (A.Length > 6) float.TryParse(A[6], style, inv, out depth);
                    float skirt = float.NaN;
                    if (A.Length > 7 && !A[7].Equals("auto", System.StringComparison.OrdinalIgnoreCase)
                        && !float.TryParse(A[7], style, inv, out skirt))
                    {
                        args.Context.AddString($"{LevelCommand}: skirt must be a width in m, 0 (none) or 'auto'");
                        return;
                    }
                    string line;
                    // Anything outside the stamps (the ground survey, the skirt plan) logs its stack too, not only a message.
                    // The pad is one player-edit undo step (a test site can undo its pad in a finally).
                    FatUndo.BeginPlayerEdit("a level pad");
                    try { line = LevelPad(x, z, sx, sz, y, Mathf.Clamp(depth, 0.5f, 24f), skirt, force); }
                    catch (System.Exception ex)
                    {
                        line = $"[FiresVoxel] level pad threw before or after its stamps: {ex.GetType().Name}: {ex.Message}";
                        FiresLogger.LogError(line + "\n" + ex);
                    }
                    finally { FatUndo.EndPlayerEdit(); }
                    args.Context?.AddString(line);
                });
        }

        // ── va_voxel_ramp (tests make their own slope, e.g. a 20°
        // ramp) ─ a tilted plane with the same V-tool brush the level pad uses: it starts at the ground under (x, z) and rises
        // (slope > 0) or falls (slope < 0) along dirDeg (0 = +z north, 90 = +x east) for `length` m, `width` m wide. Ground
        // below the plane is filled up to it (down to `depth` m), ground above is cut down to it. One undo step. One line:
        //   [FiresVoxel] ramp at (x,z) dir D deg LxW m slope S deg: y A -> B, N stamp(s)
        private const string RampCommand = "va_voxel_ramp";
        private const float RampStampRadius = 1.5f, RampStampSpacing = 1f, RampMaxSlope = 60f;
        // The ramp's own disc (a 0.5 x 1 m grid of 0.75 m discs still covers every point: farthest 0.56 m)
        private const float RampDiscRadius = 0.75f;

        internal static void RegisterRampCommand()
        {
            new Terminal.ConsoleCommand(RampCommand,
                "<x> <z> <dirDeg> <length> <width> <slopeDeg> [depth: ignored since 0.2.808] - a ramp from the ground at (x,z) along dirDeg (0 = north), "
                + "rising slopeDeg (negative falls), with the V-tool brush (admin)",
                args =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var style = System.Globalization.NumberStyles.Float;
                    float[] v = new float[6];
                    bool ok = args.Args.Length >= 7;
                    for (int i = 0; ok && i < 6; i++) ok = float.TryParse(args.Args[i + 1], style, inv, out v[i]);
                    if (!ok)
                    {
                        args.Context?.AddString($"{RampCommand} <x> <z> <dirDeg> <length> <width> <slopeDeg> [depth=6]");
                        return;
                    }
                    float depth = LevelDefaultDepth * 0.75f;
                    if (args.Args.Length > 7) float.TryParse(args.Args[7], style, inv, out depth);
                    string line;
                    FatUndo.BeginPlayerEdit("a ramp");
                    try { line = Ramp(v[0], v[1], v[2], v[3], v[4], v[5], Mathf.Clamp(depth, 0.5f, 24f)); }
                    catch (System.Exception ex)
                    {
                        line = $"[FiresVoxel] ramp threw: {ex.GetType().Name}: {ex.Message}";
                        FiresLogger.LogError(line + "\n" + ex);
                    }
                    finally { FatUndo.EndPlayerEdit(); }
                    args.Context?.AddString(line);
                });
        }

        public static string Ramp(float x, float z, float dirDeg, float length, float width, float slopeDeg, float depth)
        {
            if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) return Refused("not an admin");
            if (length < 1f || width < 1f || length > 128f || width > 64f) return Refused("length must be 1-128 m and width 1-64 m");
            if (Mathf.Abs(slopeDeg) > RampMaxSlope) return Refused($"slope must be within ±{RampMaxSlope:0} degrees");
            var generator = WorldGenerator.instance;
            if (generator == null) return Refused("no world generator");
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            float a = dirDeg * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
            var across = new Vector2(along.y, -along.x);
            float rise = Mathf.Tan(slopeDeg * Mathf.Deg2Rad);
            float y0 = LevelGround(generator, x, z);
            s_stampFailures = 0;
            ResetStampCounts();
            s_firstStampFailure = null;
            int stamps = 0;
            // No depth cap, as with the pad. The fill used to stop at plane - depth (6 m by default,
            // 24 at most), so a ramp over a drop hung with gaps under it; and the ground was read only near the generator's
            // height. Each column now surveys its disc on the layer of the ramp's start (FiresTerrainLayers) with the widening
            // window, fills from 1 m into its lowest ground and cuts past its highest, clipped only to that layer and the world.
            // `depth` is accepted and ignored.
            var layer = FiresTerrainLayers.Resolve(new Vector3(x, y0, z));
            VoxelMeshPipeline.BeginBatch();   // each touched chunk remeshed once for the whole ramp
            try
            {
            for (float s = 0f; s <= length + 0.01f; s += RampStampSpacing * 0.5f)   // half steps along (with RampDiscRadius)
            {
                float plane = y0 + s * rise;
                for (float t = -width * 0.5f; t <= width * 0.5f + 0.01f; t += RampStampSpacing)
                {
                    float cx = x + along.x * s + across.x * t, cz = z + along.y * s + across.y * t;
                    if (!SurveyDisc(layer, cx, cz, RampDiscRadius, LevelGround(generator, cx, cz), plane, out float lo, out float hi)) continue;
                    // Flat-topped discs leave a step of up to tan(slope) x radius off
                    // the plane (0.4 m over read solid, or 0.4 m under read
                    // air). Discs are now 0.75 m every 0.5 m along, to their centre's plane: within tan(slope) x 0.75 m.
                    if (lo < plane - 0.05f) { StampSpan(layer, cx, cz, RampDiscRadius, lo - 1f, plane, VoxelBrushMode.Fill); stamps++; }
                    if (hi > plane + 0.05f) { StampSpan(layer, cx, cz, RampDiscRadius, plane, hi + 1f, VoxelBrushMode.Carve); stamps++; }
                }
            }
            }
            finally { VoxelMeshPipeline.EndBatch(); }
            string done = string.Format(inv, "[FiresVoxel] ramp at ({0:0.0},{1:0.0}) dir {2:0} deg {3:0}x{4:0} m slope {5:0.0} deg: y {6:0.00} -> {7:0.00}, {8} stamp(s)",
                x, z, dirDeg, length, width, slopeDeg, y0, y0 + length * rise, stamps);
            done += "; " + StampCounts();
            if (s_stampFailures > 0)
            {
                done += $"; {s_stampFailures} stamp(s) THREW, the ramp is incomplete (first: {s_firstStampFailure})";
                FiresLogger.LogWarning(done);
                return done;
            }
            FiresLogger.LogInfo(done);
            { float ya = y0, yb = y0 + length * rise; ScheduleBorderHeal(x, z, length + width, length + width, Mathf.Min(ya, yb) - 8f, Mathf.Max(ya, yb) + 8f, "ramp"); }
            return done;
        }

        // The line logged (and returned); starts "refused:" when nothing was done.
        public static string LevelPad(float x, float z, float sizeX, float sizeZ, float y, float depth, float skirt = float.NaN, bool force = false)
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

            // Save safety (a pad once levelled and painted ground under 56
            // player-built pieces): a pad never levels under a piece a player built (Piece creator set) inside the pad + skirt.
            // It refuses, naming the count and the nearest; a manual admin can pass --force.
            if (!force)
            {
                var near = new System.Collections.Generic.List<Piece>();
                float reach = Mathf.Sqrt((halfX + skirt) * (halfX + skirt) + (halfZ + skirt) * (halfZ + skirt));
                Piece.GetAllPiecesInRadius(new Vector3(x, y, z), reach, near);
                int built = 0; Piece nearest = null; float best = float.MaxValue;
                foreach (var p in near)
                {
                    if (p == null || p.GetCreator() == 0L) continue;
                    var pp = p.transform.position;
                    if (Mathf.Abs(pp.x - x) > halfX + skirt || Mathf.Abs(pp.z - z) > halfZ + skirt) continue;
                    built++;
                    float d = new Vector2(pp.x - x, pp.z - z).magnitude;
                    if (d < best) { best = d; nearest = p; }
                }
                if (built > 0)
                {
                    string refusal = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[FiresVoxel] level pad at ({0:0.0},{1:0.0}) {2:0}x{3:0} m refused: {4} player-built piece(s) inside the pad and its {5:0.0} m skirt "
                        + "(nearest {6} at {7:0.0} m from the centre). Pick another spot, or a manual admin may add --force.",
                        x, z, sizeX, sizeZ, built, skirt, nearest != null ? Utils.GetPrefabName(nearest.gameObject) : "?", best);
                    FiresLogger.LogInfo(refusal);
                    return refusal;
                }
            }

            // A 76x76 pad levelled to y 75.90 beside a hydrology pond at 77.8 flooded the whole yard.
            // A cut below standing water that it borders becomes that water's bed: the voxel water store spreads generated
            // water into every dug pocket it touches, as a real lake would. So no pad is cut under water: any inland water
            // within the pad and its skirt whose surface stands above y is refused, naming the spot. Pass a y above it, or
            // move the pad.
            // There is no refusal over water any more (clicking a circle fills it in with land): no
            // "would sit under water" refusal. The pad levels whatever is under it; water the
            // pad's top sits below is only noted at Debug, once.
            for (float dz = -halfZ - skirt; dz <= halfZ + skirt + 0.01f; dz += PadWaterProbeStep)
                for (float dx = -halfX - skirt; dx <= halfX + skirt + 0.01f; dx += PadWaterProbeStep)
                    if (VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(x + dx, z + dz, 0.05f, false, out float surface, out _)
                        && surface > y + PadWaterMargin)
                    {
                        FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "[FiresVoxel] level pad at y {0:0.00}: inland water at ({1:0},{2:0}) stands at {3:0.00}, above the pad; levelling anyway",
                            y, x + dx, z + dz, surface));
                        dz = float.MaxValue;
                        break;
                    }
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
            ResetStampCounts();
            s_firstStampFailure = null;
            // On a mountainside the pad refused to level over a certain point, and the side skirts were not
            // properly filled, leaving giant broken gaps: there is NO depth cap any more. The pad used to
            // fill only down to y - depth and cut only up to y + depth (depth 8 by default, 24 at most), so a pad over a 40 m
            // drop hung in the air with open gaps under it and a mountain shoulder was cut 8 m and left standing; the skirt's
            // fill was capped the same way (target - depth), which is what tore holes in its sides over deep water. Now each
            // stamp column surveys the ground under its own disc (on the layer the pad's centre is on, FiresTerrainLayers),
            // fills from 1 m into its lowest ground up to y and cuts from y past its highest ground, however far that is, in
            // LevelSegment-tall cylinders, clipped only to that layer and the world. `depth` is accepted and ignored.
            var layer = FiresTerrainLayers.Resolve(new Vector3(x, LevelGround(generator, x, z), z));
            float fillLo = float.MaxValue, cutHi = float.MinValue;
            VoxelMeshPipeline.BeginBatch();   // each touched chunk remeshed once for the whole pad + skirt
            try
            {
            for (float cz = z - sizeZ * 0.5f + LevelStampSpacing * 0.5f; cz < z + sizeZ * 0.5f + LevelStampSpacing * 0.5f; cz += LevelStampSpacing)
                for (float cx = x - sizeX * 0.5f + LevelStampSpacing * 0.5f; cx < x + sizeX * 0.5f + LevelStampSpacing * 0.5f; cx += LevelStampSpacing)
                {
                    if (!SurveyDisc(layer, cx, cz, LevelStampRadius, LevelGround(generator, cx, cz), y, out float clo, out float chi)) continue;
                    if (clo < y - 0.05f) { float b = clo - 1f; StampSpan(layer, cx, cz, LevelStampRadius, b, y, VoxelBrushMode.Fill); fillLo = Mathf.Min(fillLo, b); }
                    if (chi > y + 0.05f) { float t = chi + 1f; StampSpan(layer, cx, cz, LevelStampRadius, y, t, VoxelBrushMode.Carve); cutHi = Mathf.Max(cutHi, t); }
                    stamps++;
                }

            foreach (var s in skirtStamps)
            {
                float g = s.z, target = s.w;
                if (g > target)
                    StampSpan(layer, s.x, s.y, SkirtStampRadius, target, g + 1f, VoxelBrushMode.Carve);
                else
                    StampSpan(layer, s.x, s.y, SkirtStampRadius, g - 1f, target, VoxelBrushMode.Fill);   // down INTO the ground: no gap
            }
            }
            finally { VoxelMeshPipeline.EndBatch(); }
            bool fill = fillLo < float.MaxValue, cut = cutHi > float.MinValue;
            float bottom = fillLo, top = cutHi;
            string done = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[FiresVoxel] level pad at ({0:0.0},{1:0.0}) {2:0}x{3:0} m to y={4:0.00}: ground {5:0.00}-{6:0.00}, {7}, {8}, {9} stamp(s), {10}",
                x, z, sizeX, sizeZ, y, lo, hi,
                fill ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "filled {0:0.00}-{1:0.00}", bottom, y) : "no fill",
                cut ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "cut {0:0.00}-{1:0.00}", y, top) : "no cut", stamps,
                skirt > 0f ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "skirt {0:0.0} m ({1} stamp(s))", skirt, skirtStamps.Count) : "no skirt");
            done += "; " + StampCounts();
            if (s_stampFailures > 0)
            {
                done += $"; {s_stampFailures} stamp(s) THREW, the pad is incomplete (first: {s_firstStampFailure})";
                FiresLogger.LogWarning(done);
                return done;
            }
            FiresLogger.LogInfo(done);
            ScheduleBorderHeal(x, z, sizeX * 0.5f + SkirtMaxWidth + 2f, sizeZ * 0.5f + SkirtMaxWidth + 2f, Mathf.Min(lo, y) - 8f, Mathf.Max(hi, y) + 8f, "level pad");
            return done;
        }

        // A pad once threw "Object reference not set…" and the caller kept no
        // stack: one stamp that throws no longer aborts the pad. The first failure is logged WITH its stack, stamp and
        // chunk key, the other stamps still run, and the result line counts the failures, so a partial pad is visible.
        private static int s_stampFailures;
        private static string s_firstStampFailure;

        // Large levelled areas ended up with gaps at the chunk borders: a pad or a
        // ramp writes many chunks at once; a neighbour whose border ring did not take the same edit (or whose mesh was built before it
        // did) meshes its side of the 32 m border from different samples, so the two Surface-Nets meshes leave a hairline gap along the
        // whole border. Always on: 1.5 s and again 6 s after the edit,
        // every Full chunk in the edited box (plus a margin) re-mirrors its border rings from its live neighbours (MirrorBordersNow,
        // the same heal as `va_voxel_seamscan heal`) and is remeshed, so both sides share the border vertices. Proof: one Info line
        // per pass, and fat_terrain_seam_test on the pad (0 holes, 0 step).
        private struct BorderHeal { public float X, Z, HalfX, HalfZ, YLo, YHi, At; public int Pass; public string What; }
        private static readonly System.Collections.Generic.List<BorderHeal> s_borderHeals = new System.Collections.Generic.List<BorderHeal>();

        internal static void ScheduleBorderHeal(float x, float z, float halfX, float halfZ, float yLo, float yHi, string what)
        {
            s_borderHeals.Add(new BorderHeal { X = x, Z = z, HalfX = halfX, HalfZ = halfZ, YLo = yLo, YHi = yHi, At = Time.realtimeSinceStartup + 1.5f, Pass = 1, What = what });
        }

        private static void TickBorderHeal()
        {
            if (s_borderHeals.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = s_borderHeals.Count - 1; i >= 0; i--)
            {
                var h = s_borderHeals[i];
                if (now < h.At) continue;
                s_borderHeals.RemoveAt(i);
                float C = VoxelChunk.CELLS;
                int x0 = Mathf.FloorToInt((h.X - h.HalfX) / C) - 1, x1 = Mathf.FloorToInt((h.X + h.HalfX) / C) + 1;
                int z0 = Mathf.FloorToInt((h.Z - h.HalfZ) / C) - 1, z1 = Mathf.FloorToInt((h.Z + h.HalfZ) / C) + 1;
                int y0 = Mathf.FloorToInt(h.YLo / C) - 1, y1 = Mathf.FloorToInt(h.YHi / C) + 1;
                int chunks = 0, healedChunks = 0, samples = 0, remeshed = 0;
                for (int kx = x0; kx <= x1; kx++)
                    for (int kz = z0; kz <= z1; kz++)
                        for (int ky = y0; ky <= y1; ky++)
                        {
                            if (!VoxelWorld.TryGetChunk(new Vector3Int(kx, ky, kz), out var c) || c == null || c.Kind != ChunkKind.Full || c.PendingGenerate) continue;
                            chunks++;
                            int n = c.MirrorBordersNow(true);
                            if (n > 0) { healedChunks++; samples += n; }
                            VoxelMeshPipeline.EnqueueRemesh(c);   // a stale mesh can seam even when the data already agrees
                            remeshed++;
                        }
                FiresLogger.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[FiresVoxel] border heal pass {0} after the {1} at ({2:0.0},{3:0.0}): {4} chunk(s) in the box, {5} had {6} border sample(s) out of step and were re-mirrored, {7} remeshed (always on since 0.2.941: a fix, not an option)",
                    h.Pass, h.What, h.X, h.Z, chunks, healedChunks, samples, remeshed));
                // A 1.5-2 m voxel mound once rose in the middle of a camp: the voxel surface at the
                // edit's centre and 4 points at half its reach, each pass, so a rise after the level is timed and located.
                {
                    var sb = new System.Text.StringBuilder();
                    float yh = (h.YLo + h.YHi) * 0.5f, rr = Mathf.Min(h.HalfX, h.HalfZ) * 0.5f;
                    for (int q = 0; q < 5; q++)
                    {
                        float px = h.X + (q == 1 ? rr : q == 2 ? -rr : 0f), pz = h.Z + (q == 3 ? rr : q == 4 ? -rr : 0f);
                        sb.Append(VoxelWorld.TryFindSurfaceY(px, pz, yh, 24f, out float sy) ? sy.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "none").Append(q < 4 ? " " : "");
                    }
                    FiresLogger.LogInfo($"[FiresVoxel] {h.What} at ({h.X:0.0},{h.Z:0.0}) pass {h.Pass}: voxel surface centre/E/W/N/S = {sb}");
                }
                if (h.Pass == 1) { h.Pass = 2; h.At = now + 4.5f; s_borderHeals.Add(h); }
            }
        }

        // A pad levelled up to a closed zone's border changed nothing on its border
        // row and still reported "filled / cut": outside a V-tool stroke ApplyBrush refuses any stamp whose circle reaches
        // into a closed zone (VoxelWorld.ApplyBrush). A stamp that does is split into SubStamp-radius cylinders on a SubStamp
        // grid, and only those clear of the closed zone are applied, so the open side is levelled up to the border line and
        // nothing is ever edited over the closed sea. Every stamp is counted (changed / already so / refused at a closed
        // zone) for the result line.
        private const float SubStamp = 1f;
        private static int s_stampChanged, s_stampSame, s_stampRefused;

        private static void ResetStampCounts() { s_stampChanged = 0; s_stampSame = 0; s_stampRefused = 0; }

        private static string StampCounts()
            => s_stampChanged == 0 && s_stampRefused == 0 ? "nothing changed (the ground already matches)"
            : $"{s_stampChanged} stamp(s) changed terrain, {s_stampSame} already matched"
              + (s_stampRefused > 0 ? $", {s_stampRefused} clipped at a closed zone's border" : "");

        private static string LevelStamp(Vector3 c, float radius, float height, VoxelBrushShape shape, VoxelBrushMode mode)
        {
            if (VoxelWorld.VToolScope > 0 || !Zones.ZoneModeRefusals.TerrainEditTouchesClosed(c, radius))
                return Count(LevelStampOne(c, radius, height, shape, mode));
            string last = null;
            bool any = false;
            for (float dz = -radius + SubStamp * 0.5f; dz < radius; dz += SubStamp)
                for (float dx = -radius + SubStamp * 0.5f; dx < radius; dx += SubStamp)
                {
                    if (dx * dx + dz * dz > radius * radius) continue;
                    var p = new Vector3(c.x + dx, c.y, c.z + dz);
                    if (Zones.ZoneModeRefusals.TerrainEditTouchesClosed(p, SubStamp)) { s_stampRefused++; continue; }
                    last = LevelStampOne(p, SubStamp, height, shape, mode);
                    if (IsChange(last)) any = true;
                }
            if (any) s_stampChanged++; else s_stampSame++;
            return last ?? "Refused: the whole stamp lies at a closed zone's border.";
        }

        private static bool IsChange(string result) => result != null && result != "threw" && !result.Contains("changed nothing") && !result.StartsWith("Refused");

        private static string Count(string result)
        {
            if (result != null && result.StartsWith("Refused")) s_stampRefused++;
            else if (IsChange(result)) s_stampChanged++;
            else if (result != "threw") s_stampSame++;
            return result;
        }

        // The ApplyBrush summary, or "threw" (the failure is counted and its first one logged with the stack).
        private static string LevelStampOne(Vector3 c, float radius, float height, VoxelBrushShape shape, VoxelBrushMode mode)
        {
            try { return VoxelWorld.ApplyBrush(c, radius, height, shape, mode); }
            catch (System.Exception ex)
            {
                s_stampFailures++;
                if (s_firstStampFailure != null) return "threw";
                var key = new Vector3Int(Mathf.FloorToInt(c.x / 32f), Mathf.FloorToInt(c.y / 32f), Mathf.FloorToInt(c.z / 32f));
                s_firstStampFailure = $"{mode} r={radius:0.0} at ({c.x:0.0},{c.y:0.0},{c.z:0.0}) chunk {key}: {ex.GetType().Name}: {ex.Message}";
                FiresLogger.LogError($"[FiresVoxel] level pad stamp threw: {s_firstStampFailure}\n{ex}");
                return "threw";
            }
        }

        // ── Vlevel: one dab of the level brush ──────────────────────────────────────────────────────────────────────
        // The pad's rule on one brush disc: survey the ground under it (centre + four rings of 12), then fill from the lowest
        // ground up to the target and cut from the target up past the highest ground, however far that is (no cap).
        // Already-level ground does nothing. No skirt, as with Infinity Hammer's level. Returns the Debug line:
        //   [FiresVoxel] dab level at (x,y,z) r=R target y=T: ground LO-HI, fill A-T, cut T-B, N segment(s) queued
        // and a "...: done" line when its last segment has run.
        private static readonly float[] LevelSurveyRings = { 0.25f, 0.5f, 0.75f, 0.95f };

        // The level should go further than 8 m either way, with no real
        // limit: no cut or fill cap. The height clamps only to the world (LevelWorldBottom..LevelWorldTop); the cut and the
        // fill run the whole way, in LevelSegment-tall cylinders, LevelSegmentsPerFrame a frame (PumpLevel), so a 200 m
        // column never stalls a frame. The survey window spans the clicked ground AND the target, so the ground is found
        // however far the target is from it.
        private const float LevelWorldBottom = -500f, LevelWorldTop = 2000f, LevelSegment = 24f;
        private const int LevelSegmentsPerFrame = 2;

        private struct LevelJob
        {
            internal Vector3 Centre;
            internal float Radius, Height;
            internal VoxelBrushMode Mode;
        }

        private static readonly Queue<LevelJob> s_levelJobs = new Queue<LevelJob>();
        private static string s_levelHead;
        private static int s_levelTotal, s_levelDone, s_levelChanged;

        internal static float ClampLevelHeight(float y) => Mathf.Clamp(y, LevelWorldBottom, LevelWorldTop);

        private static void QueueColumn(Vector3 at, float radius, float bottom, float top, VoxelBrushMode mode)
        {
            for (float y0 = bottom; y0 < top - 0.01f; y0 += LevelSegment)
            {
                float y1 = Mathf.Min(top, y0 + LevelSegment + 0.5f);   // segments overlap 0.5 m: no seam between them
                s_levelJobs.Enqueue(new LevelJob { Centre = new Vector3(at.x, (y0 + y1) * 0.5f, at.z), Radius = radius, Height = y1 - y0, Mode = mode });
                s_levelTotal++;
            }
        }

        // Each frame: the next LevelSegmentsPerFrame cylinders of the queued level; one Debug line when the level is done.
        private static void PumpLevel()
        {
            if (s_levelJobs.Count == 0) return;
            // The queued segments run inside the undo scope, so the whole level is one undo step.
            FatUndo.BeginPlayerEdit("a Vlevel stroke"); VoxelWorld.VToolScope++;
            try { PumpLevelSegments(); }
            finally { VoxelWorld.VToolScope--; FatUndo.EndPlayerEdit(); }
        }

        private static void PumpLevelSegments()
        {
            for (int i = 0; i < LevelSegmentsPerFrame && s_levelJobs.Count > 0; i++)
            {
                var job = s_levelJobs.Dequeue();
                string result = LevelStamp(job.Centre, job.Radius, job.Height, VoxelBrushShape.Cylinder, job.Mode);
                s_levelDone++;
                if (result != null && !result.Contains("changed nothing") && result != "threw") s_levelChanged++;
            }
            if (s_levelJobs.Count > 0) return;
            FiresLogger.LogDebug($"{s_levelHead}: done, {s_levelDone}/{s_levelTotal} segment(s), {s_levelChanged} changed voxels"
                + (s_stampFailures > 0 ? $", {s_stampFailures} THREW (first: {s_firstStampFailure})" : ""));
        }

        internal static string LevelDab(Vector3 at, float radius, float target)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            target = ClampLevelHeight(target);
            // All the tools affect only the layer they are raycast on: the level keeps to the
            // layer of the point it was aimed at (FiresTerrainLayers): in a cave, under its roof; on ground over a cave, above
            // the cave's ceiling (it never fills down into it); on a skyland, that island's body. The target, the survey
            // window, the fill and the cut are all clipped to the layer's band.
            var layer = FiresTerrainLayers.Resolve(at);
            target = layer.Clamp(target);
            string head = string.Format(inv, "[FiresVoxel] dab level at ({0:0.0},{1:0.0},{2:0.0}) r={3:0.0} target y={4:0.00} on {5}",
                at.x, at.y, at.z, radius, target, layer.Describe());
            // The survey reads the top solid surface (TrySampleSurfaceY) in a window from below the lower of the clicked ground
            // and the target to above the higher one, clipped to the layer: on a sky island it is the island's top, never the
            // ground far below; in a cave its floor, never the hill above the roof; over a cave the ground, never the cave floor.
            // A point with no surface in the window (past an island's edge, over a pit) is left out of the survey.
            float windowTop = Mathf.Max(at.y, target) + LevelSurfaceReach, windowBottom = Mathf.Min(at.y, target) - LevelSurfaceReach;
            windowTop = Mathf.Min(windowTop, layer.Ceiling + FiresTerrainLayers.RoofKeep - 0.25f);
            windowBottom = Mathf.Max(windowBottom, layer.Floor - 0.25f);
            float lo = float.MaxValue, hi = float.MinValue;
            int found = 0;
            for (int ringIndex = -1; ringIndex < LevelSurveyRings.Length; ringIndex++)
            {
                float ringR = ringIndex < 0 ? 0f : radius * LevelSurveyRings[ringIndex];
                for (int i = 0; i < (ringIndex < 0 ? 1 : 12); i++)
                {
                    float a = i * Mathf.PI / 6f;
                    // A sample whose ground rises past the window widens it (SurfaceInLayer); it used to be dropped,
                    // so the cut stopped at window height on a mountainside.
                    if (!SurfaceInLayer(layer, at.x + Mathf.Cos(a) * ringR, at.z + Mathf.Sin(a) * ringR, windowTop, windowBottom, out float h))
                        continue;
                    found++;
                    lo = Mathf.Min(lo, h);
                    hi = Mathf.Max(hi, h);
                }
            }
            if (found == 0)
                return head + string.Format(inv, ": no voxel surface between {0:0.0} and {1:0.0} under the brush, nothing done", windowBottom, windowTop);
            // A raised pad over shallow sea did not fill in the side skirts on the cliffs in all
            // spots and left giant broken gaps, so the fill's bottom also reads a ring just OUTSIDE the disc. The disc's own
            // samples stop at 0.95 r, so on a drop the cylinder's rim stood above the lower ground beyond it, and the outer
            // wall of a drag hung with open gaps beneath it. Now the wall reaches the lowest ground at its foot.
            float footLo = lo;
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.PI / 6f;
                if (SurfaceInLayer(layer, at.x + Mathf.Cos(a) * radius * LevelFootRing, at.z + Mathf.Sin(a) * radius * LevelFootRing, windowTop, windowBottom, out float h))
                    footLo = Mathf.Min(footLo, h);
            }

            // A level DRAG queues dab after dab, so a new dab appends to the queue (the counters restart only when
            // the queue had drained).
            if (s_levelJobs.Count == 0)
            {
                s_stampFailures = 0;
                s_firstStampFailure = null;
                s_levelTotal = s_levelDone = s_levelChanged = 0;
            }
            string fill = "no fill", cut = "no cut";
            if (lo < target - 0.05f)
            {
                float bottom = footLo - 1f, top = target;   // 1 m into the lowest ground at the disc's foot, so the fill joins it
                if (layer.ClampSpan(ref bottom, ref top))
                {
                    QueueColumn(at, radius, bottom, top, VoxelBrushMode.Fill);
                    fill = string.Format(inv, "fill {0:0.00}-{1:0.00}", bottom, top);
                }
            }
            if (hi > target + 0.05f)
            {
                float bottom = target, top = hi + 1f;   // past the highest ground
                if (layer.ClampSpan(ref bottom, ref top) && top > bottom + 0.05f)
                {
                    QueueColumn(at, radius, bottom, top, VoxelBrushMode.Carve);
                    cut = string.Format(inv, "cut {0:0.00}-{1:0.00}", bottom, top);
                }
            }
            s_levelHead = head;
            string line = string.Format(inv, "{0}: ground {1:0.00}-{2:0.00}, {3}, {4}, {5} segment(s) of up to {6:0} m queued ({7} a frame)",
                head, lo, hi, fill, cut, s_levelTotal, LevelSegment, LevelSegmentsPerFrame);
            PumpLevel();
            return line;
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

        // 0.2.1087 (FDT: a 1,290 ms VoxelBrushDrag.Update): any Update over SlowUpdateMs logs (Debug, at most one line per 2 s)
        // its time split into the queue pumps and the tool part, so the next slow frame names its stage.
        private const double SlowUpdateMs = 30.0;
        private static readonly long[] s_updMarks = new long[9];
        private static float s_updNextLine;

        private static void UpdMark(int i) => s_updMarks[i] = System.Diagnostics.Stopwatch.GetTimestamp();

        private void Update()
        {
            UpdMark(0);
            try { UpdateBody(); }
            finally
            {
                long end = System.Diagnostics.Stopwatch.GetTimestamp();
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency, ms = (end - s_updMarks[0]) * f;
                if (ms >= SlowUpdateMs && FiresLogger.DebugEnabled && Time.unscaledTime >= s_updNextLine)
                {
                    s_updNextLine = Time.unscaledTime + 2f;
                    string[] names = { "water tick", "level", "tests+heal", "skin", "river carve", "layer paint", "islands map", "tool" };
                    var sb = new System.Text.StringBuilder($"[FiresVoxel] slow V-tool update {ms:F0} ms:");
                    for (int i = 1; i <= 8; i++)
                    {
                        long a = s_updMarks[i - 1], b = i < 8 ? s_updMarks[i] : end;
                        if (a == 0 || b < a) continue;
                        double st = (b - a) * f;
                        if (st >= 1.0) sb.Append($" {names[i - 1]} {st:F0} ms;");
                    }
                    FiresLogger.LogDebug(sb.ToString());
                }
                System.Array.Clear(s_updMarks, 0, s_updMarks.Length);
            }
        }

        private void UpdateBody()
        {
            WaterShapingTools.Tick();
            UpdMark(1);
            PumpLevel();   // a level's queued cylinders finish even if the tool is put away
            UpdMark(2);
            TickTestCommands();   // delayed fat_level_verify, fat_goto's exact landing
            TickBorderHeal();   // chunk-border heal after a pad / ramp
            UpdMark(3);
            PumpSkin();    // Vskin's queued shell stamps
            UpdMark(4);
            WaterToolDriver.PumpRiver();   // 0.2.1087: a Vriver click-mode carve, a time-budgeted slice per frame
            UpdMark(5);
            VoxelLayerPaint.Pump();   // a cave's or a skyland's queued paint
            UpdMark(6);
            FiresMinimapIslands.Tick();   // the map's skylands follow the island set
            UpdMark(7);
            TickGate();    // EVERY frame, so the release that clears it is seen
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
            // The stroke framework keys its drivers by the hammer piece's name; a stroke ends when its tool changes.
            RegisterTerrainDrivers();
            WaterToolDriver.Register();   // Vpour / Vriver / Vpool / Vpond (WaterToolDriver.cs)
            var selectedPiece = player != null && player.m_buildPieces != null ? player.m_buildPieces.GetSelectedPiece() : null;   // 0.2.1058 (audit T5): read once
            string piece = selectedPiece != null ? selectedPiece.m_name : null;
            EndStrokeIfToolChanged(piece);

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

            if (tool != Tool.Pond && !IsWaterTool(tool)) PondTool.Hide();
            if (tool != Tool.Fall) ToolFalls.HidePreview();   // 0.2.1086: the Vfall ghost shows only while Vfall is held
            if (tool == Tool.None || refused
                || (FiresVoxelConfig.Enabled != null && !FiresVoxelConfig.Enabled.Value))
            {
                PondTool.Hide();
                SetGizmo(Tool.None, Vector3.zero, false);
                UpdateHintRay(Tool.None, Vector3.zero);
                SetRulerVisualHidden(false);
                EndStroke();
                return;
            }

            // THE IH DIVORCE — our own cursor (crosshair raycast) and our own
            // scroll radius. No ruler, no console arming.
            Vector3 cursor;
            if (virtualPress) cursor = s_pressCursor;
            else if (!TileBrush.TryGetPointerWorldPosition(out cursor))
            {
                PondTool.Hide();
                ToolFalls.HidePreview();
                SetGizmo(Tool.None, Vector3.zero, false);
                if (VerdantsAscent.Terrain.FatInput.PrimaryHeld) Blocked(tool, "the crosshair hits no ground within 600 m");
                return;
            }
            bool carveMod = VerdantsAscent.Terrain.FatInput.AltHeld || (virtualPress && s_pressAlt);
            SetRulerVisualHidden(true);   // IH's white ring never draws under our ghosts

            // Plain scroll = UNIFORM size (camera zoom is suppressed by the
            // VoxelToolPieces ZInput patch while a V-tool is selected): the
            // whole shape scales — radius AND cylinder height keep their
            // aspect. PgUp/PgDn changes the aspect (explicit height).
            // Wheel rule shared by every radius tool: mouse wheel = radius, Shift+wheel = height, one height value; no
            // Ctrl+wheel, no separate top/bottom: plain wheel sizes the radius only; Shift+wheel moves the tool's ONE height
            // value, the same one PgUp/PgDn move (a stroke driver's offset, Vlevel's height, the cylinder's height), one step a
            // notch. A tool with no height ignores Shift+wheel. PgUp/PgDn keep working.
            float scroll = VerdantsAscent.Terrain.FatInput.WheelAxis();
            s_heightWheelNotches = 0f;
            // Pointer-over-UI only counts while the cursor is free (see ReadyToAct).
            if (Mathf.Abs(scroll) > 0.001f && !VerdantsAscent.Terrain.FatInput.CtrlHeld && !VerdantsAscent.Terrain.FatInput.AltHeld   // Core RadiusWheel: Ctrl/Alt = no wheel
                && (!Cursor.visible
                    || UnityEngine.EventSystems.EventSystem.current == null
                    || !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()))
            {
                if (VerdantsAscent.Terrain.FatInput.ShiftHeld)
                    s_heightWheelNotches = Mathf.Sign(scroll) * Mathf.Max(1f, Mathf.Round(Mathf.Abs(scroll) / 0.1f));
                else
                    VoxelToolPieces.ToolRadius = Mathf.Clamp(VoxelToolPieces.ToolRadius * (1f + scroll * 1.4f), 0.5f, 32f);
            }

            // Sculpting tools at a manageable draw speed: a tool with
            // a stroke driver (VoxelBrushDrag.Strokes.cs) runs its preview, offset and click-and-drag stroke there. The
            // branches below stay for the tools without one yet (water, pond, fall, mop).
            if (VToolDrivers.TryGet(piece, out var driver))
            {
                DriveStroke(tool, piece, driver, player, cursor, carveMod, virtualPress);
                return;
            }
            if (tool == Tool.Level)
            {
                // Level has the same flat radius and height adjustment as Infinity
                // Hammer's nudge, PgUp/PgDn move the level height from the ground under the crosshair (Shift: fine steps);
                // scroll sizes the flat circle. The preview disc stands at that height and radius.
                // No range cap: the height clamps only to the world (ClampLevelHeight). A press steps once;
                // held, it repeats faster the longer it is held, so 100 m is a couple of seconds, not 200 presses.
                float step = VerdantsAscent.Terrain.FatInput.ShiftHeld ? LevelFineStep : LEVEL_OFFSET_STEP;
                float before = s_levelOffset;
                int dir = VerdantsAscent.Terrain.FatInput.Key(GhostUp) ? 1 : VerdantsAscent.Terrain.FatInput.Key(GhostDown) ? -1 : 0;
                if (VerdantsAscent.Terrain.FatInput.KeyDown(GhostUp) || VerdantsAscent.Terrain.FatInput.KeyDown(GhostDown))
                {
                    s_levelOffset += dir * step;
                    s_levelHeldSince = Time.unscaledTime;
                }
                else if (dir != 0 && Time.unscaledTime - s_levelHeldSince > LevelRepeatDelay)
                {
                    float held = Time.unscaledTime - s_levelHeldSince - LevelRepeatDelay;
                    s_levelOffset += dir * step * LevelRepeatRate * (1f + held * held) * Time.unscaledDeltaTime;
                }
                s_levelOffset += s_heightWheelNotches * LEVEL_OFFSET_STEP;   // Shift+wheel
                s_levelOffset = Mathf.Clamp(s_levelOffset, LevelWorldBottom - cursor.y, LevelWorldTop - cursor.y);
                if (!Mathf.Approximately(before, s_levelOffset) && player != null)
                    player.Message(MessageHud.MessageType.TopLeft, $"Level height {s_levelOffset:+0.0;-0.0;0} m from the ground (y {cursor.y + s_levelOffset:0.0}), radius {EffectiveGizmoRadius():0.0} m", 0, null, false);
            }
            else if (tool == Tool.Cylinder)
            {
                if (VerdantsAscent.Terrain.FatInput.KeyDown(GhostUp)) VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight + 1f, -24f, 24f);
                if (VerdantsAscent.Terrain.FatInput.KeyDown(GhostDown)) VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight - 1f, -24f, 24f);
                if (s_heightWheelNotches != 0f) VoxelToolPieces.CylHeight = Mathf.Clamp(VoxelToolPieces.CylHeight + s_heightWheelNotches, -24f, 24f);
            }
            // The water tools' PgUp/PgDn, previews and clicks are their stroke drivers' (WaterToolDriver.cs).

            if (tool == Tool.Cube || tool == Tool.Sphere)
            {
                SetGizmo(tool, cursor, carveMod);
                UpdateHintRay(tool, cursor);
            }
            else if (tool == Tool.Fall || tool == Tool.Mop)
            {
                // Lines-only ring of the fall's width at the cursor (vanilla Remove = middle mouse removes falls in it).
                s_cylRadius = VoxelToolPieces.ToolRadius;
                s_cylSignedHeight = LevelGhostThickness;
                SetGizmo(Tool.Cylinder, cursor, true);
                UpdateHintRay(tool, cursor);
                if (tool == Tool.Fall)
                {
                    // 0.2.1086 (Fire: "v fall should show an actual ghost preview of what the fall will look like so we can position it"):
                    // the fall the click would lay (spillway ring, see-through curtain down to the landing, pool ring), from the
                    // same plan as the click; red spillway ring and no curtain when the click would be refused.
                    Vector3 ghostView = GameCamera.instance != null ? GameCamera.instance.transform.forward
                        : Player.m_localPlayer != null ? Player.m_localPlayer.transform.forward : Vector3.forward;
                    ToolFalls.Preview(cursor, ghostView, VoxelToolPieces.ToolRadius, SharedLineMat());
                }
                if (tool == Tool.Fall && ZInput.GetButtonDown("Remove"))
                {
                    // 0.2.1072 ([lead] + [visual] review): remove uses the build tools' own Remove input (vanilla middle mouse), not a
                    // modifier (LeftAlt broke our key rules, Ctrl is vanilla crouch); Numpad 7/9 undo/redo through the shared history.
                    string removed = ToolFalls.RemoveNear(cursor, VoxelToolPieces.ToolRadius);
                    FiresLogger.LogDebug(removed);
                    FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[FiresWaterfall] Vfall Remove at ({0:0.0},{1:0.0},{2:0.0}) r {3:0.0}: {4}", cursor.x, cursor.y, cursor.z, VoxelToolPieces.ToolRadius, removed));
                    if (player != null) player.Message(MessageHud.MessageType.TopLeft, removed.Contains(" removed ") ? "Waterfall removed" : "No waterfall here to remove", 0, null);
                }
                if (tool == Tool.Mop && ZInput.GetButtonDown("Remove"))
                {
                    // 0.2.1073 (our key rules: no Alt): Vmop's RESTORE is the build tools' Remove input (vanilla middle mouse) on the
                    // ring, as Vfall's remove; it was LeftAlt + drag. One press = one ring restored.
                    float rr = VoxelToolPieces.ToolRadius;
                    int cols = VoxelWorld.MopWater(cursor, rr, true, out _);
                    int drawn = VerdantsAscent.Water.Core.WaterMeshCombiner.RefreshMopsIn(cursor, rr);
                    VerdantsAscent.Water.Core.PlacedWaterSheets.MarkDirtyIn(cursor, rr);   // 0.2.1097: the drawn sheet comes back at once
                    VerdantsAscent.Water.Core.PlacedWaterSolids.MarkAllDirty("brush drag");
                    if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(cursor, rr + 2f);
                    FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[FiresWater] Vmop restore (Remove) at ({0:0.0},{1:0.0},{2:0.0}) r={3:0.0}: {4} column(s) unmopped, {5} drawn water bod(ies) rebuilt",
                        cursor.x, cursor.y, cursor.z, rr, cols, drawn));
                    // 0.2.1334: Restore only lifts masks; a disk the mop deleted is not a mask, it comes back with undo.
                    if (player != null) player.Message(MessageHud.MessageType.TopLeft, (cols > 0 ? "Mopped water restored" : "Nothing mopped here to restore") + ". Deleted water comes back with undo", 0, null);
                }
            }
            else if (tool == Tool.Level)
            {
                // A thin solid disc at the height the ground will end at: the cursor before the click, the stroke's
                // target while the button is held.
                var ghost = cursor;
                ghost.y = _strokeActive ? s_levelTarget : cursor.y + s_levelOffset;
                s_cylRadius = EffectiveGizmoRadius();   // the disc is the circle the click levels
                s_cylSignedHeight = LevelGhostThickness;
                SetGizmo(Tool.Cylinder, ghost, false);
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
            if (virtualPress && !VerdantsAscent.Terrain.FatInput.PrimaryHeld) _armAfterRelease = false;
            if (!VerdantsAscent.Terrain.FatInput.PrimaryHeld && !virtualPress)
            {
                _strokeActive = false;
                FlushMopStroke();   // 0.2.1334
                _armAfterRelease = false;   // selecting-click released — a fresh press now carves
                return;
            }
            // Still holding the button that selected the tool: show the ghost but
            // don't carve until it's released and pressed again. The local flag
            // only catches a tool CHANGE; the Core gate also covers re-picking
            // the tool you already had and the click that closes the hammer menu
            // (both of which used to carve on the spot).
            if (_armAfterRelease) { Blocked(tool, "the click that picked the tool is still held (release, then press again)"); return; }
            if (!ReadyToAct(out string gate)) { Blocked(tool, gate); return; }

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
                if (tool == Tool.Level) s_levelTarget = ClampLevelHeight(cursor.y + s_levelOffset);
                Dispatch(tool, cursor, carveMod);
                _lastDabPos = cursor;
                _strokeActive = true;
                return;
            }

            // Vpool is a CLICK tool, not a stroke: one pool per press, and the
            // next press connects to it. Dragging must never chain a pool every
            // few metres, which is what the dab pump below would do.
            // Vlevel too: one flat circle per click, as Infinity Hammer's level places one.
            if (tool == Tool.Pool || tool == Tool.Fall || tool == Tool.Level || tool == Tool.Pond) return;   // Vfall too: one fall per click

            // One click used to carve a massive tunnel. The budget grew by DrawSpeed × 8 dabs EVERY FRAME, and
            // the crosshair ray followed the hole it had just dug, so a held click kept digging deeper along the view ray.
            // Now: a per-SECOND draw speed per tool family (config), and a new dab only when the cursor has moved SIDEWAYS
            // (across the view ray) by the spacing: holding still on one spot never tunnels or towers.
            _dabBudget = Mathf.Min(_dabBudget + DrawSpeedFor(tool) * Time.deltaTime, 1.5f);
            Vector3 view = GameCamera.instance != null ? GameCamera.instance.transform.forward : Vector3.forward;
            while (_dabBudget >= 1f)
            {
                Vector3 delta = cursor - _lastDabPos;
                Vector3 sideways = delta - Vector3.Dot(delta, view) * view;
                float len = sideways.magnitude;
                if (len < spacing) break;
                Vector3 dab = _lastDabPos + delta * Mathf.Min(1f, spacing / len);   // FULL 3D step — strokes still climb walls
                Dispatch(tool, dab, carveMod);
                _lastDabPos = dab;
                _dabBudget -= 1f;
            }
        }

        // Carve worked once on the islands, then after switching tools and back it stopped working, on the normal voxel
        // ground too (the log showed nine Vcarve dabs, then after the next hammer
        // selection not one V-tool line of any kind). The V-tools used Core's FiresPlaceGate, which re-arms every frame while
        // ANY uGUI element sits under the pointer or a FiresInputBlock token is held. With the cursor locked in play the
        // pointer is the screen centre, so a panel another tool left there (or a capture token never released) blocked every
        // V-tool for good, silently. This gate keeps the rule that matters (after a menu, the inventory, the pause menu or a
        // text field, the mouse must come up first) and counts the pointer-over-UI and capture tests only while the cursor is
        // FREE, when a UI really owns the click.
        // Every V-tool was once stuck "waiting for the mouse to come up after the hammer menu": it was updated
        // this gate only from inside the held-button path, so it never saw the button UP and, once a menu armed it, it never
        // cleared. Now GateStep runs once EVERY frame from the top of Update, held or not; it is a pure step on (a selection
        // UI is open, a mouse button is held), the same rule as before: a UI open (or closing this frame) arms it, and only a
        // frame with both buttons up clears it.
        private static bool s_gateNeedsRelease, s_gateMenuWasOpen;
        private static string s_gateWhy;
        private static int s_gateTickedFrame = -1;

        internal struct GateState { internal bool NeedsRelease, MenuWasOpen; }

        internal static GateState GateStep(GateState s, bool uiOpen, bool buttonHeld)
        {
            if (uiOpen || s.MenuWasOpen) s.NeedsRelease = true;
            s.MenuWasOpen = uiOpen;
            if (s.NeedsRelease && !buttonHeld) s.NeedsRelease = false;
            return s;
        }

        private static void TickGate()
        {
            if (Time.frameCount == s_gateTickedFrame) return;
            s_gateTickedFrame = Time.frameCount;
            bool open = SelectionUiOpen(out string which);
            if (open) s_gateWhy = which;
            var s = GateStep(new GateState { NeedsRelease = s_gateNeedsRelease, MenuWasOpen = s_gateMenuWasOpen },
                open, VerdantsAscent.Terrain.FatInput.PrimaryHeld || VerdantsAscent.Terrain.FatInput.SecondaryHeld);
            s_gateNeedsRelease = s.NeedsRelease;
            s_gateMenuWasOpen = s.MenuWasOpen;
        }

        private static bool ReadyToAct(out string why)
        {
            TickGate();
            why = s_gateNeedsRelease ? $"waiting for the mouse to come up after {s_gateWhy ?? "a menu"}" : null;
            return !s_gateNeedsRelease;
        }

        private static bool SelectionUiOpen(out string which)
        {
            which = null;
            try
            {
                if (Hud.IsPieceSelectionVisible()) which = "the hammer menu";
                else if (InventoryGui.IsVisible()) which = "the inventory";
                else if (Menu.IsVisible()) which = "the pause menu";
                else if (TextInput.IsVisible()) which = "a text field";
                else if (global::Console.IsVisible()) which = "the console";
                else if (Chat.instance != null && Chat.instance.HasFocus()) which = "the chat";
                else if (Cursor.visible)   // Valheim leaves lockState None in play; a hidden cursor is the in-play signal
                {
                    if (FiresCore.Input.FiresInputBlock.IsCapturing) which = "a Fires panel holding input";
                    else
                    {
                        var events = UnityEngine.EventSystems.EventSystem.current;
                        if (events != null && events.IsPointerOverGameObject()) which = "the pointer over " + UiUnderPointer(events);
                    }
                }
            }
            catch { }
            return which != null;
        }

        private static string UiUnderPointer(UnityEngine.EventSystems.EventSystem events)
        {
            try
            {
                var data = new UnityEngine.EventSystems.PointerEventData(events) { position = Input.mousePosition };
                var hits = new List<UnityEngine.EventSystems.RaycastResult>();
                events.RaycastAll(data, hits);
                return hits.Count > 0 && hits[0].gameObject != null ? $"'{hits[0].gameObject.name}'" : "a UI element";
            }
            catch { return "a UI element"; }
        }

        // A held click on a V-tool that did nothing says why (Debug, at most every BlockedReportSeconds), so the next "it
        // doesn't work" names its gate.
        private const float BlockedReportSeconds = 2f;
        private static float s_nextBlockedReport;

        private static void Blocked(Tool tool, string why)
        {
            if (Time.realtimeSinceStartup < s_nextBlockedReport) return;
            s_nextBlockedReport = Time.realtimeSinceStartup + BlockedReportSeconds;
            // Core's place gate is not consulted by the V-tools, so it is not printed here.
            FiresLogger.LogDebug($"[FiresVoxel] V-tool {tool} click held but not acting: {why}; cursor {(Cursor.visible ? "visible" : "hidden")} "
                + $"({Cursor.lockState}), Fires input capture {FiresCore.Input.FiresInputBlock.IsCapturing}");
        }

        // Dabs per second while the button is held (FiresVoxelConfig "Voxel Terrain - Tools").
        private static float DrawSpeedFor(Tool tool)
        {
            switch (tool)
            {
                case Tool.Cube:
                case Tool.Sphere:
                    return FiresVoxelConfig.ShapeDrawSpeed != null ? FiresVoxelConfig.ShapeDrawSpeed.Value : 3f;
                case Tool.River:
                case Tool.Pour:
                case Tool.Pool:
                    return FiresVoxelConfig.WaterDrawSpeed != null ? FiresVoxelConfig.WaterDrawSpeed.Value : 4f;
                case Tool.Level:
                    return FiresVoxelConfig.LevelDrawSpeed != null ? FiresVoxelConfig.LevelDrawSpeed.Value : 3f;
                case Tool.Mop:
                    return FiresVoxelConfig.MopDrawSpeed != null ? FiresVoxelConfig.MopDrawSpeed.Value : 4f;
                default:
                    if (s_cylMode == VoxelBrushMode.Fill)
                        return FiresVoxelConfig.FillDrawSpeed != null ? FiresVoxelConfig.FillDrawSpeed.Value : 3f;
                    return FiresVoxelConfig.CarveDrawSpeed != null ? FiresVoxelConfig.CarveDrawSpeed.Value : 3f;
            }
        }

        // One Debug line per dab (proves what each tool does): which tool, what it asked for, and what the voxel
        // world says it changed locally. "changed nothing" warnings stay where they are (ApplyBrush).
        private static void Evidence(Tool tool, VoxelBrushMode mode, Vector3 at, float radius, string result)
        {
            FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[FiresVoxel] dab {0} {1} at ({2:0.0},{3:0.0},{4:0.0}) r={5:0.0} -> {6}",
                tool, mode, at.x, at.y, at.z, radius, result ?? "no result"));
        }

        private void Dispatch(Tool tool, Vector3 pos, bool carveMod)
        {
            if (s_pressing) s_pressDabs++;
            DispatchNow(tool, pos, carveMod);
        }

        // ApplyBrush seeds a chunk it finds missing from the GROUND alone (SeedFromGround has no
        // sky island in it), so a dab reaching an island chunk that is not loaded yet would leave an air hole through the
        // island. A dab whose box touches any unloaded chunk inside an island's band is skipped (Debug line); a moment
        // later, once the island streams in, the same dab goes through.
        private static bool IslandChunkMissing(Vector3 c, float radius, float halfHeight, out Vector3Int missing)
        {
            missing = default;
            float r = radius + 1f, h = halfHeight + 1f;
            for (int kz = Mathf.FloorToInt((c.z - r) / 32f); kz <= Mathf.FloorToInt((c.z + r) / 32f); kz++)
                for (int ky = Mathf.FloorToInt((c.y - h) / 32f); ky <= Mathf.FloorToInt((c.y + h) / 32f); ky++)
                    for (int kx = Mathf.FloorToInt((c.x - r) / 32f); kx <= Mathf.FloorToInt((c.x + r) / 32f); kx++)
                    {
                        var key = new Vector3Int(kx, ky, kz);
                        if (!FiresSkyIslands.ChunkOverlaps(new Vector3(kx * 32f, ky * 32f, kz * 32f))) continue;
                        if (VoxelWorld.TryGetChunk(key, out var chunk) && chunk != null) continue;
                        missing = key;
                        return true;
                    }
            return false;
        }

        private static bool SkipForIsland(Tool tool, Vector3 c, float radius, float halfHeight)
        {
            if (!IslandChunkMissing(c, radius, halfHeight, out var key)) return false;
            FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[FiresVoxel] dab {0} at ({1:0.0},{2:0.0},{3:0.0}) skipped: sky-island chunk {4} is not loaded yet (a ground seed would hole the island)",
                tool, c.x, c.y, c.z, key));
            return true;
        }

        // Building is allowed over a closed chunk, so V tools are too: every V-tool edit runs inside
        // VoxelWorld.VToolScope, the one case ApplyBrush lets reach over a closed zone (hoe / IH / WEC stay refused there).
        // FatUndo's prefix keeps targeting DispatchNow by name.
        private void DispatchNow(Tool tool, Vector3 pos, bool carveMod)
        {
            VoxelWorld.VToolScope++;
            try { DispatchCore(tool, pos, carveMod); }
            finally { VoxelWorld.VToolScope--; }
        }

        private void DispatchCore(Tool tool, Vector3 pos, bool carveMod)
        {
            float reach = tool == Tool.Cylinder ? s_cylRadius : EffectiveGizmoRadius();
            float halfH = tool == Tool.Cylinder ? Mathf.Max(reach, Mathf.Abs(s_cylSignedHeight))
                : tool == Tool.Level ? Mathf.Abs(pos.y - s_levelTarget) * 0.5f + LevelSurfaceReach : reach;
            if (tool != Tool.Pour && tool != Tool.Mop   // river and pool carve with ApplyBrush too; pour and mop never seed a chunk
                && SkipForIsland(tool, tool == Tool.Level ? new Vector3(pos.x, (pos.y + s_levelTarget) * 0.5f, pos.z) : pos, reach, halfH))
                return;
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
                            Evidence(tool, s_cylMode, c, s_cylRadius,
                                VoxelWorld.ApplyBrush(c, s_cylRadius, h, VoxelBrushShape.Cylinder, s_cylMode));
                        }
                        else
                        {
                            // Default = a sphere of the brush radius, but one dab reaches only "Dab step depth" past the
                            // ground at the cursor (carve is slow per click): carve sits the sphere UP
                            // by (r - step), fill sits it DOWN by (r - step). Restore keeps the full sphere.
                            Vector3 c = pos;
                            float step = FiresVoxelConfig.DabStepDepth != null ? FiresVoxelConfig.DabStepDepth.Value : s_cylRadius;
                            float lift = Mathf.Max(0f, s_cylRadius - step);
                            if (s_cylMode == VoxelBrushMode.Carve) c.y += lift;
                            else if (s_cylMode == VoxelBrushMode.Fill) c.y -= lift;
                            Evidence(tool, s_cylMode, c, s_cylRadius,
                                VoxelWorld.ApplyBrush(c, s_cylRadius, 0f, VoxelBrushShape.Sphere, s_cylMode));
                        }
                        break;
                    }
                    case Tool.Cube:
                    {
                        var m = carveMod ? VoxelBrushMode.Carve : VoxelBrushMode.Fill;
                        Evidence(tool, m, pos, EffectiveGizmoRadius(), VoxelWorld.ApplyBrush(pos, EffectiveGizmoRadius(), 0f, VoxelBrushShape.Cube, m));
                        break;
                    }
                    case Tool.Sphere:
                    {
                        var m = carveMod ? VoxelBrushMode.Carve : VoxelBrushMode.Fill;
                        Evidence(tool, m, pos, EffectiveGizmoRadius(), VoxelWorld.ApplyBrush(pos, EffectiveGizmoRadius(), 0f, VoxelBrushShape.Sphere, m));
                        break;
                    }
                    case Tool.Level:
                        FiresLogger.LogDebug(LevelDab(pos, EffectiveGizmoRadius(), s_levelTarget));
                        break;
                    case Tool.Mop:
                    {
                        // A radius tool that removes generated or drawn water
                        // permanently: one persisted mask for both kinds (VoxelWorld.MopWater); drawn water rebuilds
                        // around it at once here, and on the other peers when the chunk anchor arrives.
                        // 0.2.1073: a mop stroke only mops (restore moved to the Remove input; Alt is not ours to use).
                        carveMod = false;
                        float r = VoxelToolPieces.ToolRadius;
                        int cols = VoxelWorld.MopWater(pos, r, carveMod, out int wetGen);
                        NoteMopDisks(pos, r, cols);   // 0.2.1334: whole disks in the ring are deleted, not stacked under a mask
                        // 0.2.1073 (Fire: "vmop doesnt work on waterfalls"): MopWater only masks water columns + drawn water; a Vfall is a
                        // separate per-chunk record (ToolFalls), so the falls whose lip is in the ring go too (curtain, mist and its pool
                        // follow the record). Same stroke, same chunk records, so one Numpad 7 brings them back.
                        string fallsGone = ToolFalls.RemoveNear(pos, r);
                        if (fallsGone.Contains(" removed ")) FiresLogger.LogDebug("[FiresWater] Vmop over falls: " + fallsGone);
                        int drawn = VerdantsAscent.Water.Core.WaterMeshCombiner.RefreshMopsIn(pos, r);
                        // An old cave pool's pour stayed under Vmop, so a mop also removes
                        // the FiresLiquidWater pours in its ring. A restore never deletes them.
                        if (!carveMod) VerdantsAscent.Water.Core.FiresLiquidWaterPrefab.RemovePoursIn(pos, r);
                        FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            carveMod
                                ? "[FiresWater] mop restore at ({0:0.0},{1:0.0},{2:0.0}) r={3:0.0}: {4} column(s) unmopped, {6} drawn water bod(ies) rebuilt; recorded"
                                : "[FiresWater] mop at ({0:0.0},{1:0.0},{2:0.0}) r={3:0.0}: removed {5} generated column(s), {6} drawn water bod(ies) rebuilt dry; recorded {4} column(s)",
                            pos.x, pos.y, pos.z, r, cols, wetGen, drawn));
                        // 0.2.1097 (Fire, shot 550: "vmop still doesnt work to precision clean water that spills over"): the mop only
                        // reached the DRAWN placed sheet through a remesh of the voxel chunks the mask changed (MopColumns marks Full
                        // chunks only; the sheet chunk of the water's own layer may not be one of them), so a spill could keep its old
                        // sheet. Now every drawn sheet chunk, the walk colliders and the clutter over the ring rebuild at once, and the
                        // field (PlacedWaterField: IsMoppedAt over the water's layer and its neighbours) answers dry there to every
                        // consumer (sheet, level / swim, life, nav, state).
                        int sheets = VerdantsAscent.Water.Core.PlacedWaterSheets.MarkDirtyIn(pos, r);
                        VerdantsAscent.Water.Core.PlacedWaterSolids.MarkAllDirty("brush drag");
                        if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(pos, r + 2f);
                        FiresLogger.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "[Vmop] {0} {1} column(s) in ring r {2:0.0} at {3:0.0},{4:0.0} (y {5:0.0}; disks touched {6}, drawn sheet chunk(s) rebuilt {7}, generated wet {8})",
                            carveMod ? "restored" : "cleared", cols, r, pos.x, pos.z, pos.y, drawn, sheets, wetGen));
                        break;
                    }
                    case Tool.Fall:
                    {
                        Vector3 view = GameCamera.instance != null ? GameCamera.instance.transform.forward
                            : Player.m_localPlayer != null ? Player.m_localPlayer.transform.forward : Vector3.forward;
                        // 0.2.1072: a click only places; remove is vanilla's Remove input (see the Fall preview), Numpad 7 undoes.
                        string line = ToolFalls.Place(pos, view, VoxelToolPieces.ToolRadius);
                        FiresLogger.LogDebug(line);
                        // 0.2.1072: one line per Vfall click with the outcome (placed / removed / refused and why).
                        FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "[FiresWaterfall] Vfall click at ({0:0.0},{1:0.0},{2:0.0}) r {3:0.0}{4}: {5}", pos.x, pos.y, pos.z, VoxelToolPieces.ToolRadius,
                            "",
                            line.Contains(" refused: ") ? "refused (" + line.Substring(line.IndexOf(" refused: ") + 10) + ")"
                                : line.Contains(" removed ") ? "removed" : "placed (" + line + ")"));
                        // A refusal says why on screen too: a fall needs a lip, a drop and ground below.
                        if (line.Contains(" refused: ") && Player.m_localPlayer != null && !s_pressing)
                            Player.m_localPlayer.Message(MessageHud.MessageType.Center, line.Substring(line.IndexOf(" refused: ") + 10), 0, null, false);
                        break;
                    }
                    // Pond / River / Pour / Pool dispatch from their stroke drivers (WaterToolDriver.cs).
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
                    return Tool.Sphere;   // a sphere at the aim point (CarveSphereDriver)
                case "Vfill":
                case "Vdelete":
                    return Tool.Cylinder;
                case "Vcube":
                    return Tool.Cube;
                case "Vsphere":
                    return Tool.Sphere;
                case "Vlevel":
                    return Tool.Level;
                case "Vskin":
                    return Tool.Skin;   // a stroke driver (VoxelBrushDrag.Strokes.cs)
                case "Vramp":
                    return Tool.Skin;   // 0.2.1057: a stroke driver too (VoxelRamp.cs)
                case "Vfall":
                    return Tool.Fall;
                case "Vmop":
                    return Tool.Mop;
                case "Vriver":
                    return Tool.River;
                case "Vpour":
                    return Tool.Pour;
                case "Vpool":
                    return Tool.Pool;
                case "Vpond":
                    return Tool.Pond;
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
            HideCylinderGizmo();   // the true cylinder ghost (DrawGhost shows it again after this when it is the shape)
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
        // reference design. Lines live under a "cage" child scaled 2%
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
