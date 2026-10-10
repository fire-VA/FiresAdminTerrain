using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Terrain.Persistence;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Testing
{
    // One tool operation of admintools_test: what it does at its cell, which points every peer reads back, and what the
    // tester's own reading must show afterwards. Each case owns one cell, so results never mix, and each goes through the
    // same path the player's tool does: FAT's tile brushes through TileBrushRPC (routed to every peer, the tile owner
    // writes the ZDO), the voxel tools through VoxelWorld.ApplyBrush (the edit persists as the chunk's anchor ZDO), and
    // vanilla tools by spawning the terrain-op piece the hoe, cultivator or pickaxe spawns.
    internal sealed class ToolCase
    {
        internal string Family;
        internal string Name;
        internal Func<Vector3, List<ProbePoint>> Points;
        internal Func<Vector3, IEnumerator> Apply;
        internal Func<Vector3, float[], float[], string> Check;
        // Why the case can't run here (a mod it drives isn't installed), or null.
        internal Func<string> Requires;
        // How long the result may take to appear (a pour settles over seconds); 0 = the runner's default.
        internal float SettleSeconds;
    }

    internal static class AdminToolsCases
    {
        internal const string TileFamily = "tile", VoxelFamily = "voxel", VanillaFamily = "vanilla", InfinityFamily = "ih";

        // Brush sizes fit a 12 m cell with room for falloff; heights are what a player would plausibly do.
        private const float BrushRadius = 2.5f;
        private const float EdgeOffset = 1.5f;
        private const float RaiseMetres = 2f;
        private const float FlatAbove = 1.5f;
        private const float SmoothBump = 3f;
        private const float SmoothBumpRadius = 1.5f;
        private const float SmoothStrength = 1f;
        private const float NoiseIntensity = 1.5f;
        private const float NoiseScale = 4f;
        private const float GrabMetres = 1.5f;
        private const float SlopeMetres = 3f;
        private const float SlopeSmooth = 0f;
        // Inside the flat, so a ring of flat vertices is left for Patch Hole to copy from (it inherits from edited vertices
        // up to 3 m away; R75 punched the whole flat and the patch correctly had nothing to copy).
        private const float HolePunchRadius = 1.5f;
        // Radians; TileBrush tilts along sin(angle)*x + cos(angle)*z, so a quarter turn falls along +x, where TilePoints'
        // edge point sits (R75: angle 0 tilted along z and the edge read "+0.00 m").
        private const float SlopeAngle = Mathf.PI / 2f;
        private const float VoxelSphereRadius = 2.5f;
        private const float VoxelCubeHalf = 2f;
        private const float VoxelFillLift = 1f;
        private const float VanillaSettleSeconds = 1f;
        internal const float HeightTolerance = 0.35f;
        private const float MinimumChange = 0.2f;
        private const float HalfMask = 0.5f;
        private const byte FullChannel = 255;
        private static readonly Color32 PavedPaint = new Color32(0, 0, FullChannel, FullChannel);

        internal static List<ToolCase> All()
        {
            var cases = new List<ToolCase>();
            // No TileCases(): the 2D tile layer is retired in FAT 0.2.651 (Fire, 2026-09-29); its tools no longer exist to test.
            cases.AddRange(VoxelCases());
            cases.AddRange(VanillaCases());
            cases.AddRange(InfinityCases());
            cases.AddRange(BotToolCases());
            cases.AddRange(AdminToolsWaterCases.All());
            return cases;
        }

        internal static string HasCommand(string command)
            => Terminal.commands.ContainsKey(command) ? null : $"'{command}' is not a console command on this client";

        // The ground the cell stood on before the run touched it: the voxel surface where there is one, else vanilla's.
        internal static float Ground(float[] values, int voxelIndex, int vanillaIndex)
            => !float.IsNaN(values[voxelIndex]) ? values[voxelIndex] : values[vanillaIndex];

        // The height a tile brush starts from at the centre: the nearest vertex's stored height when an earlier edit left one
        // (b[0]), else the brush centre's y, which TileBrush uses for untouched vertices. Not the voxel surface: the places
        // keep earlier runs' tile edits until a clean runs there.
        private static float TileBase(Vector3 c, float[] b) => !float.IsNaN(b[0]) ? b[0] : c.y;

        // ── FAT tile brushes ────────────────────────────────────────────────────────────────────────────────────
        private static IEnumerable<ToolCase> TileCases()
        {
            yield return Tile("tile_raise", c => TileBrushRPC.SendRaise(c, BrushRadius, RaiseMetres, 1f),
                (c, b, a) => ExpectDome(a[0], TileBase(c, b), RaiseMetres, "tile at the centre"));
            yield return Tile("tile_lower", c => TileBrushRPC.SendRaise(c, BrushRadius, -RaiseMetres, 1f),
                (c, b, a) => ExpectDome(a[0], TileBase(c, b), -RaiseMetres, "tile at the centre"));
            yield return Tile("tile_flat", c => TileBrushRPC.SendFlat(c, BrushRadius, c.y + FlatAbove, false),
                (c, b, a) => Expect(a[0], c.y + FlatAbove, "tile at the centre"));
            yield return Tile("tile_smooth", c =>
                {
                    TileBrushRPC.SendRaise(c, SmoothBumpRadius, SmoothBump, 1f);
                    TileBrushRPC.SendSmooth(c, BrushRadius, SmoothStrength);
                },
                (c, b, a) => float.IsNaN(a[0]) ? "no tile at the centre after the bump and the smooth"
                    : a[0] < TileBase(c, b) + SmoothBump - MinimumChange ? null
                    : $"the {SmoothBump:0.#} m bump was not smoothed: centre {a[0] - TileBase(c, b):+0.00;-0.00} m");
            yield return Tile("tile_noise", c => TileBrushRPC.SendNoise(c, BrushRadius, NoiseIntensity, NoiseScale),
                (c, b, a) => float.IsNaN(a[0]) && float.IsNaN(a[3]) ? "noise left no tile at the centre or the edge" : null);
            yield return Tile("tile_grab", c => TileBrushRPC.SendGrab(c, BrushRadius, GrabMetres),
                (c, b, a) => Expect(a[0], TileBase(c, b) + GrabMetres, "tile at the centre"));
            yield return Tile("tile_slope", c => TileBrushRPC.SendSlope(c, BrushRadius, SlopeAngle, c.y, SlopeMetres, SlopeSmooth, true),
                (c, b, a) => float.IsNaN(a[0]) || float.IsNaN(a[3]) ? "the slope left no tile at the centre or the edge"
                    : Mathf.Abs(a[3] - a[0]) > MinimumChange ? null : $"no fall across the slope ({a[3] - a[0]:+0.00;-0.00} m)");
            yield return new ToolCase
            {
                Family = TileFamily, Name = "tile_paint",
                Points = c => new List<ProbePoint> { new ProbePoint(ProbeKind.TilePaint, c), new ProbePoint(ProbeKind.TileHeight, c) },
                Apply = c => Once(() =>
                {
                    TileBrushRPC.SendFlat(c, BrushRadius, c.y, false);
                    TileBrushRPC.SendPaint(c, BrushRadius, PavedPaint, false);
                }),
                Check = (c, b, a) => float.IsNaN(a[0]) ? "no painted tile vertex at the centre"
                    : AdminToolsProbe.UnpackColour(a[0]).b >= FullChannel * HalfMask ? null
                    : $"the centre reads paint {Colour(a[0])}, not paved",
            };
            yield return new ToolCase
            {
                Family = TileFamily, Name = "tile_biome",
                Points = c => new List<ProbePoint> { new ProbePoint(ProbeKind.TileBiome, c), new ProbePoint(ProbeKind.Biome, c) },
                Apply = c => Once(() => TileBrushRPC.SendBiome(c, BrushRadius, (ushort)Heightmap.Biome.Swamp)),
                Check = (c, b, a) => (int)a[0] == (int)Heightmap.Biome.Swamp ? null : $"the tile's biome at the centre is {(int)a[0]}, not Swamp",
            };
            yield return new ToolCase
            {
                Family = TileFamily, Name = "tile_hole",
                Points = TilePoints,
                Apply = c => Steps(
                    () => TileBrushRPC.SendFlat(c, BrushRadius, c.y + FlatAbove, false),
                    () => TileBrushRPC.SendHolePunch(c, HolePunchRadius),
                    () => TileBrushRPC.SendHolePatch(c, BrushRadius)),
                Check = (c, b, a) => float.IsNaN(a[0]) ? "the patched hole left no tile at the centre" : a[0] >= c.y - HeightTolerance ? null : $"the patched tile at the centre ({a[0]:0.00}) sits below the ground it patched ({c.y:0.00})",
            };
            yield return Tile("tile_island", c => TileBrushRPC.SendFlat(c, BrushRadius, c.y + FlatAbove, false, islandMode: true),
                (c, b, a) => Expect(a[0], c.y + FlatAbove, "island tile at the centre"));
            yield return Tile("tile_clear", c =>
                {
                    TileBrushRPC.SendRaise(c, BrushRadius, RaiseMetres, 1f);
                    TileBrushRPC.SendClear(c, BrushRadius);
                },
                (c, b, a) => float.IsNaN(a[0]) ? null : $"a tile still stands at the centre ({a[0]:0.00}) after the clear");
            yield return new ToolCase
            {
                Family = TileFamily, Name = "tile_undo",
                Points = TilePoints,
                Apply = c => Steps(
                    () => TileBrushRPC.SendRaise(c, BrushRadius, RaiseMetres, 1f),
                    () => TileUndoLog.Undo(),
                    () => TileUndoLog.Redo()),
                Check = (c, b, a) => ExpectDome(a[0], TileBase(c, b), RaiseMetres, "tile at the centre after undo + redo"),
            };
        }

        private static List<ProbePoint> TilePoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.TileHeight, c),
            new ProbePoint(ProbeKind.VoxelSurface, c),
            new ProbePoint(ProbeKind.VanillaHeight, c),
            new ProbePoint(ProbeKind.TileHeight, c + Vector3.right * EdgeOffset),
            new ProbePoint(ProbeKind.Standing, c),
        };

        private static ToolCase Tile(string name, Action<Vector3> apply, Func<Vector3, float[], float[], string> check) => new ToolCase
        {
            Family = TileFamily, Name = name, Points = TilePoints, Apply = c => Once(() => apply(c)), Check = check,
        };

        // ── FAT voxel tools (the branded pieces' own brush) ─────────────────────────────────────────────────────
        private static IEnumerable<ToolCase> VoxelCases()
        {
            yield return Voxel("vcarve", c => VoxelWorld.ApplyBrush(c, VoxelSphereRadius, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve),
                (c, b, a) => Falls(a[0], b[0], "the voxel surface at the centre"));
            yield return Voxel("vfill", c => VoxelWorld.ApplyBrush(c + Vector3.up * VoxelFillLift, VoxelSphereRadius, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Fill),
                (c, b, a) => Rises(a[0], b[0], "the voxel surface at the centre"));
            yield return new ToolCase
            {
                Family = VoxelFamily, Name = "vdelete",
                Points = VoxelPoints,
                Apply = c => Steps(
                    () => VoxelWorld.ApplyBrush(c, VoxelSphereRadius, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Carve),
                    () => VoxelWorld.ApplyBrush(c, VoxelSphereRadius + 1f, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Restore)),
                Check = (c, b, a) => Expect(a[0], b[0], "the voxel surface back at the generated ground"),
            };
            // Judged by Standing (the ground a body lands on): the VoxelSurface probe searches from the cell's height and finds
            // the ground under the cube (R69: VoxelSurface 60.00 on every peer while all stood at 64.00).
            yield return Voxel("vcube", c => VoxelWorld.ApplyBrush(c + Vector3.up * VoxelCubeHalf, VoxelCubeHalf, 0f, VoxelBrushShape.Cube, VoxelBrushMode.Fill),
                (c, b, a) => Expect(a[2], c.y + VoxelCubeHalf * 2f, "the cube's top at the centre (standing on it)"));
            yield return Voxel("vsphere", c => VoxelWorld.ApplyBrush(c, VoxelSphereRadius, 0f, VoxelBrushShape.Sphere, VoxelBrushMode.Fill),
                (c, b, a) => Rises(a[0], b[0], "the voxel surface at the centre"));
        }

        private static List<ProbePoint> VoxelPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.VoxelSurface, c),
            new ProbePoint(ProbeKind.VoxelSurface, c + Vector3.right * EdgeOffset),
            new ProbePoint(ProbeKind.Standing, c),
        };

        private static ToolCase Voxel(string name, Action<Vector3> apply, Func<Vector3, float[], float[], string> check) => new ToolCase
        {
            Family = VoxelFamily, Name = name, Points = VoxelPoints, Apply = c => Once(() => apply(c)), Check = check,
        };

        // ── Vanilla tools (the terrain-op piece each one spawns) ────────────────────────────────────────────────
        private static IEnumerable<ToolCase> VanillaCases()
        {
            yield return Vanilla("hoe_raise", VanillaOps.HoeRaise, (c, b, a) => Rises(a[0], b[0], "vanilla ground") ?? Follows(b, a));
            // 1.0's Level Ground smooths rather than levels, so after Raise Ground it brings the raised centre back DOWN but
            // not exactly to the start.
            yield return Vanilla("hoe_level", VanillaOps.HoeLevel, (c, b, a) => Falls(a[0], b[0] + VanillaOps.HoeRaiseDelta(), "the raised vanilla ground")
                ?? Follows(b, a), pre: VanillaOps.HoeRaise);
            yield return Vanilla("hoe_paved", VanillaOps.HoePaved, (c, b, a) => Channel(a[3], 2, "paved (blue)"));
            yield return Vanilla("hoe_path", VanillaOps.HoePath, (c, b, a) => Channel(a[3], 0, "dirt (red)"));
            yield return Vanilla("cultivate", VanillaOps.Cultivate,
                (c, b, a) => a[2] == 1f ? null : $"the centre is not cultivated (reads {a[2]}), so crops can't be planted there");
            yield return Vanilla("cultivate_grass", VanillaOps.Replant,
                (c, b, a) => a[2] == 0f ? null : $"grass over a cultivated patch still reads cultivated ({a[2]})", pre: VanillaOps.Cultivate);
            yield return Vanilla("pickaxe_dig", VanillaOps.PickaxeDig, (c, b, a) => Falls(a[0], b[0], "vanilla ground") ?? Follows(b, a));
        }

        private static List<ProbePoint> VanillaPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.VanillaHeight, c),
            new ProbePoint(ProbeKind.VoxelSurface, c),
            new ProbePoint(ProbeKind.Cultivated, c),
            new ProbePoint(ProbeKind.PaintMask, c),
            new ProbePoint(ProbeKind.Standing, c),
        };

        private static ToolCase Vanilla(string name, Func<GameObject> op, Func<Vector3, float[], float[], string> check, Func<GameObject> pre = null) => new ToolCase
        {
            Family = VanillaFamily, Name = name, Points = VanillaPoints,
            Apply = c => VanillaApply(c, pre, op),
            Check = check,
        };

        private static IEnumerator VanillaApply(Vector3 cell, Func<GameObject> pre, Func<GameObject> op)
        {
            if (pre != null)
            {
                VanillaOps.Place(pre(), cell);
                yield return new WaitForSecondsRealtime(VanillaSettleSeconds);
            }
            VanillaOps.Place(op(), cell);
        }

        // ── The tools in the BOT's hands (`only tools`; Fire, 2026-09-29: "you keep testing the terrain tools on the player") ─
        // FDT 1.1.176's `bot tool <kind> <mode> <x> <z> [size] [alt]` has the bot equip the real item, pick the mode from its
        // build menu and use it through vanilla's own input path (hoe/cultivator: the ghost + UpdatePlacement's place branch;
        // pickaxe: a real swing; the Chaos Hammer's V-tools: FAT's va_voxel_press into the same brush update a click runs).
        // A case passes only when the tool FIRED (stamina/durability spent, or at least one voxel dab), the ground changed the
        // way a player expects, and all three peers read it back alike.
        internal const string ToolsFamily = "tools";
        private const float BotToolSeconds = 60f;
        private static readonly Dictionary<string, string> s_toolReplies = new Dictionary<string, string>();

        private static IEnumerable<ToolCase> BotToolCases()
        {
            yield return BotTool("tools_hoe_raise", VanillaPoints, new[] { "hoe raise" },
                (c, b, a) => Rises(a[0], b[0], "vanilla ground") ?? Follows(b, a));
            yield return BotTool("tools_hoe_level", VanillaPoints, new[] { "hoe raise", "hoe level" },
                (c, b, a) => Falls(a[0], b[0] + VanillaOps.HoeRaiseDelta(), "the raised vanilla ground") ?? Follows(b, a));
            yield return BotTool("tools_hoe_paved", VanillaPoints, new[] { "hoe paved" }, (c, b, a) => Channel(a[3], 2, "paved (blue)"));
            yield return BotTool("tools_hoe_path", VanillaPoints, new[] { "hoe path" }, (c, b, a) => Channel(a[3], 0, "dirt (red)"));
            yield return BotTool("tools_cultivate", VanillaPoints, new[] { "cultivator cultivate" },
                (c, b, a) => a[2] == 1f ? null : $"the centre is not cultivated (reads {a[2]}), so crops can't be planted there");
            yield return BotTool("tools_cultivate_grass", VanillaPoints, new[] { "cultivator cultivate", "cultivator grass" },
                (c, b, a) => a[2] == 0f ? null : $"grass over a cultivated patch still reads cultivated ({a[2]})");
            yield return BotTool("tools_pickaxe_dig", VanillaPoints, new[] { "pickaxe dig" },
                (c, b, a) => Falls(a[0], b[0], "vanilla ground") ?? Follows(b, a));
            string sphere = VoxelSphereRadius.ToString("0.##", CultureInfo.InvariantCulture);
            string cube = VoxelCubeHalf.ToString("0.##", CultureInfo.InvariantCulture);
            yield return BotTool("tools_vcarve", VoxelPoints, new[] { $"voxel vcarve {{0}} {{1}} {sphere}" },
                (c, b, a) => Falls(a[0], b[0], "the voxel surface at the centre"));
            yield return BotTool("tools_vfill", VoxelPoints, new[] { $"voxel vfill {{0}} {{1}} {sphere}" },
                (c, b, a) => Rises(a[0], b[0], "the voxel surface at the centre"));
            yield return BotTool("tools_vdelete", VoxelPoints, new[] { $"voxel vcarve {{0}} {{1}} {sphere}", $"voxel vdelete {{0}} {{1}} {sphere}" },
                (c, b, a) => Expect(a[0], b[0], "the voxel surface back at the generated ground"));
            // The V-tool cube is centred on the aim point: its top stands one half-size above the ground.
            yield return BotTool("tools_vcube", VoxelPoints, new[] { $"voxel vcube {{0}} {{1}} {cube}" },
                (c, b, a) => Expect(a[2], c.y + VoxelCubeHalf, "the cube's top at the centre (standing on it)"));
            yield return BotTool("tools_vsphere", VoxelPoints, new[] { $"voxel vsphere {{0}} {{1}} {sphere}" },
                (c, b, a) => Rises(a[0], b[0], "the voxel surface at the centre"));
        }

        // Each step is `<kind> <mode>` (the cell's x,z appended) or a format with {0}=x {1}=z for the ones that take a size.
        private static ToolCase BotTool(string name, Func<Vector3, List<ProbePoint>> points, string[] steps, Func<Vector3, float[], float[], string> ground)
            => new ToolCase
            {
                Family = ToolsFamily, Name = name, Points = points,
                Requires = () => !FdtHasBotTool() ? $"needs FDT {BotToolFdtVersion} bot tool (this client has FDT {FdtVersion() ?? "none"})"
                    : AdminToolsTest.UsesToolsItself || FatTestBot.BotUid != 0 ? null
                    : "the tools are the bot's to use: run them in the bot's own split b, or with a bot that answers 'bot follow'",
                Apply = c => BotToolApply(name, c, steps),
                Check = (c, b, a) => ToolFired(name) ?? ground(c, b, a),
            };

        // `bot tool` / `fdt_bot` arrive in FDT 1.1.177; an older FDT would leave every case waiting 60 s for no answer.
        private const string FdtGuid = "com.Fire.FiresDebugginTools";
        private static readonly System.Version BotToolFdtVersion = new System.Version(1, 1, 177);

        private static string FdtVersion()
            => BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(FdtGuid, out var info) && info?.Metadata != null
                ? info.Metadata.Version.ToString() : null;

        private static bool FdtHasBotTool()
            => BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(FdtGuid, out var info) && info?.Metadata != null
               && info.Metadata.Version >= BotToolFdtVersion;

        private static IEnumerator BotToolApply(string name, Vector3 cell, string[] steps)
        {
            var replies = new List<string>();
            string x = cell.x.ToString("0.##", CultureInfo.InvariantCulture), z = cell.z.ToString("0.##", CultureInfo.InvariantCulture);
            foreach (string step in steps)
            {
                string verb = step.Contains("{0}") ? string.Format(CultureInfo.InvariantCulture, step, x, z) : $"{step} {x} {z}";
                if (AdminToolsTest.UsesToolsItself) FatTestBot.RunLocal("tool " + verb);   // split b: this client is the bot
                else FatTestBot.Send("tool " + verb);
                yield return FatTestBot.AwaitReply(r => r.StartsWith("tool:", StringComparison.OrdinalIgnoreCase), BotToolSeconds);
                string reply = FatTestBot.LastReply ?? $"no answer to 'bot tool {verb}' in {BotToolSeconds:0} s (FDT older than 1.1.176?)";
                replies.Add(reply);
                if (ToolReplyProblem(reply) != null) break;
                yield return new WaitForSecondsRealtime(VanillaSettleSeconds);
            }
            s_toolReplies[name] = string.Join(" | ", replies);
            AdminToolsTest.Note($"tool: {s_toolReplies[name]}");
        }

        // Null when every step's reply says the tool fired.
        private static string ToolFired(string name)
        {
            if (!s_toolReplies.TryGetValue(name, out string all)) return "the bot's tool use never ran";
            foreach (string reply in all.Split(new[] { " | " }, StringSplitOptions.None))
            {
                string problem = ToolReplyProblem(reply);
                if (problem != null) return problem;
            }
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex s_spent =
            new System.Text.RegularExpressions.Regex(@"(stamina|durability) ([0-9.]+)->([0-9.]+)");
        private static readonly System.Text.RegularExpressions.Regex s_dabs =
            new System.Text.RegularExpressions.Regex(@"dabs (\d+)");

        private static string ToolReplyProblem(string reply)
        {
            if (reply == null || !reply.StartsWith("tool:", StringComparison.OrdinalIgnoreCase)) return reply ?? "no reply";
            if (reply.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0 || reply.IndexOf("NOT placed", StringComparison.Ordinal) >= 0)
                return reply;
            var dabs = s_dabs.Match(reply);
            if (dabs.Success) return int.Parse(dabs.Groups[1].Value) >= 1 ? null : $"the V-tool did not fire: {reply}";
            foreach (System.Text.RegularExpressions.Match m in s_spent.Matches(reply))
                if (float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) < float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                    return null;
            return $"the tool was used but spent no stamina or durability: {reply}";
        }

        // The voxel ground takes a vanilla edit: it moves the same way as vanilla's heightmap, within the tolerance.
        private static string Follows(float[] before, float[] after)
        {
            if (float.IsNaN(before[1]) || float.IsNaN(after[1])) return "no voxel ground here to follow the edit";
            float vanilla = after[0] - before[0], voxel = after[1] - before[1];
            return Mathf.Abs(vanilla - voxel) <= HeightTolerance ? null
                : $"the voxel ground moved {voxel:+0.00;-0.00} m where vanilla's moved {vanilla:+0.00;-0.00} m";
        }

        // ── Infinity Hammer's terrain tools (FAT's Docs\infinity_tools.yaml): World Edit Commands' `terrain` line ──
        // IH fires the tool's console line with `<area>` expanded to `from=<x>,<z>,<y> circle=<r>`; the test runs the
        // same line through the command's own action, as the console does once devcommands allow it.
        private const string TerrainCommand = "terrain";
        private const float InfinityRaise = 1f;
        private const float InfinityBigRadius = 5f;

        private static IEnumerable<ToolCase> InfinityCases()
        {
            yield return Infinity("ih_raise", c => $"raise={Num(InfinityRaise)}", BrushRadius,
                (c, b, a) => Expect(a[0], b[0] + InfinityRaise, "vanilla ground") ?? Follows(b, a));
            yield return Infinity("ih_lower", c => $"raise={Num(-InfinityRaise)}", BrushRadius,
                (c, b, a) => Expect(a[0], b[0] - InfinityRaise, "vanilla ground") ?? Follows(b, a));
            yield return new ToolCase
            {
                Family = InfinityFamily, Name = "ih_level", Points = VanillaPoints, Requires = HasTerrainCommand,
                Apply = c => Steps(
                    () => Terrain($"raise={Num(InfinityRaise)}", c, BrushRadius),
                    () => Terrain("level", c, BrushRadius)),
                Check = (c, b, a) => Expect(a[0], b[0], "vanilla ground levelled back") ?? Expect(a[1], b[1], "voxel ground levelled back"),
            };
            yield return Infinity("ih_paint_paved", c => "paint=paved", BrushRadius, (c, b, a) => Channel(a[3], 2, "paved (blue)"));
            yield return Infinity("ih_paint_dirt", c => "paint=dirt", BrushRadius, (c, b, a) => Channel(a[3], 0, "dirt (red)"));
            yield return Infinity("ih_paint_cultivated", c => "paint=cultivated", BrushRadius,
                (c, b, a) => a[2] == 1f ? null : $"the centre is not cultivated (reads {a[2]})");
            yield return Infinity("ih_paint_grass", c => "paint=grass", BrushRadius,
                (c, b, a) => a[2] == 0f ? null : $"grass still reads cultivated ({a[2]})");
            yield return new ToolCase
            {
                Family = InfinityFamily, Name = "ih_big_radius", Requires = HasTerrainCommand,
                Points = c => new List<ProbePoint>
                {
                    new ProbePoint(ProbeKind.VanillaHeight, c + Vector3.right * (InfinityBigRadius - 1f)),
                    new ProbePoint(ProbeKind.VoxelSurface, c + Vector3.right * (InfinityBigRadius - 1f)),
                },
                Apply = c => Once(() => Terrain($"raise={Num(InfinityRaise)}", c, InfinityBigRadius)),
                Check = (c, b, a) => Expect(a[0], b[0] + InfinityRaise, $"vanilla ground {InfinityBigRadius - 1f:0} m out") ?? Follows(b, a),
            };
        }

        private static ToolCase Infinity(string name, Func<Vector3, string> operation, float radius, Func<Vector3, float[], float[], string> check) => new ToolCase
        {
            Family = InfinityFamily, Name = name, Points = VanillaPoints, Requires = HasTerrainCommand,
            Apply = c => Once(() => Terrain(operation(c), c, radius)),
            Check = check,
        };

        private static string HasTerrainCommand()
            => Terminal.commands.ContainsKey(TerrainCommand) ? null : "World Edit Commands' 'terrain' command is not installed here";

        private static void Terrain(string operation, Vector3 at, float radius)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "{0} {1} from={2:0.##},{3:0.##},{4:0.##} circle={5:0.##}",
                TerrainCommand, operation, at.x, at.z, at.y, radius);
            RunConsoleLine(line);
        }

        internal static void RunConsoleLine(string line)
        {
            string word = line.Split(' ')[0].ToLowerInvariant();
            if (!Terminal.commands.TryGetValue(word, out var command)) throw new MissingCommandException(word);
            var console = global::Console.instance;
            command.RunAction(new Terminal.ConsoleEventArgs(line, console, command));
        }

        private static string Num(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        // ── Checks and helpers ──────────────────────────────────────────────────────────────────────────────────
        // A tile raise/lower: TileBrush stamps the full delta inside radius - 1 m, then runs two 1-ring smoothing passes, so a
        // 2.5 m brush's centre lands at ~75-80% of the delta (R75: 1.54 of 2 m on the island, both ways). Pass when the centre
        // moved the right way by at least DomeMinShare of the delta and not past it.
        private const float DomeMinShare = 0.6f;

        internal static string ExpectDome(float got, float from, float delta, string what)
        {
            if (float.IsNaN(got)) return $"{what}: nothing to read";
            if (float.IsNaN(from)) return $"{what}: no starting ground to measure from";
            float moved = (got - from) * Mathf.Sign(delta), full = Mathf.Abs(delta);
            return moved >= full * DomeMinShare && moved <= full + HeightTolerance ? null
                : $"{what} is {got:0.00}, moved {got - from:+0.00;-0.00} m from {from:0.00}; expected {delta * DomeMinShare:+0.00;-0.00} to {delta:+0.00;-0.00} m";
        }

        internal static string Expect(float got, float want, string what)
        {
            if (float.IsNaN(got)) return $"{what}: nothing to read";
            if (float.IsNaN(want)) return $"{what}: no starting ground to measure from";
            return Mathf.Abs(got - want) <= HeightTolerance ? null : $"{what} is {got:0.00}, expected {want:0.00}";
        }

        private static string Rises(float after, float before, string what)
            => float.IsNaN(after) || float.IsNaN(before) ? $"{what}: nothing to read"
                : after - before >= MinimumChange ? null : $"{what} did not rise ({after - before:+0.00;-0.00} m)";

        private static string Falls(float after, float before, string what)
            => float.IsNaN(after) || float.IsNaN(before) ? $"{what}: nothing to read"
                : before - after >= MinimumChange ? null : $"{what} did not fall ({after - before:+0.00;-0.00} m)";

        private static string Channel(float packed, int channel, string what)
        {
            if (float.IsNaN(packed)) return "no heightmap paint to read at the centre";
            var colour = AdminToolsProbe.UnpackColour(packed);
            byte value = channel == 0 ? colour.r : channel == 1 ? colour.g : colour.b;
            return value >= FullChannel * HalfMask ? null : $"the centre's paint is {Colour(packed)}, not {what}";
        }

        internal static string Colour(float packed)
        {
            var colour = AdminToolsProbe.UnpackColour(packed);
            return $"rgb({colour.r},{colour.g},{colour.b})";
        }

        private static IEnumerator Once(Action action)
        {
            action();
            yield break;
        }

        // Several operations of one case, a frame apart, as a player's separate clicks would be.
        private static IEnumerator Steps(params Action[] actions)
        {
            foreach (var action in actions)
            {
                action();
                yield return null;
            }
        }
    }

    // A tool's console command isn't registered here (its mod isn't installed, or this peer doesn't load it): the case
    // SKIPs rather than FAILs.
    internal sealed class MissingCommandException : Exception
    {
        internal MissingCommandException(string command) : base($"'{command}' is not a console command on this client") { }
    }
}
