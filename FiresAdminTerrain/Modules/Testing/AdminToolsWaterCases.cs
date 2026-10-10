using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Terrain.Testing
{
    // admintools_test's water, ice and snow cases, as [visual]'s Tools\WATER_TOOLS_REFERENCE.md lays them out: each tool's
    // Infinity Hammer console line (FAT's Docs\infinity_tools.yaml, `<area>` = `from=<x>,<z>,<y> circle=<r>`), read back
    // with the call that tool's water answers to, and settled by polling to a cap rather than a fixed wait.
    internal static class AdminToolsWaterCases
    {
        internal const string WaterFamily = "water";

        private const float Radius = 2.5f;
        private const float WaterHeight = 1f;
        private const float WaterfallAbove = 0.5f;
        // |surface - brush| per [visual]: 0.15 m of wave on one peer; 0.3 m between two peers' waves.
        internal const float SurfaceTolerance = 0.3f;
        private const float PresentAboveGround = 0.1f;
        private const float LandIceAbove = 0.1f;
        private const float IceRaise = 0.5f;
        private const float SnowMinAbove = 0.05f;
        private const float SnowMaxAbove = 0.5f;
        private const float DiskSettleSeconds = 5f;
        private const float VolumeSettleSeconds = 15f;
        private const float SpringSettleSeconds = 20f;

        internal static IEnumerable<ToolCase> All()
        {
            yield return Disk("water_place", "va_brush_water", CustomLiquidType.Water);
            yield return Disk("water_lava", "va_brush_lava", CustomLiquidType.Lava);
            yield return Disk("water_tar", "va_brush_water_tar", CustomLiquidType.Tar);
            yield return Disk("water_boiling", "va_brush_water_boiling", CustomLiquidType.BoilingWater);
            yield return Disk("water_freezing", "va_brush_water_freezing", CustomLiquidType.FreezingWater);
            yield return Disk("water_swimmable_lava", "va_brush_swimmable_lava", CustomLiquidType.Lava);
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "water_ice_disk", Points = WaterPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => Once(() => Line("va_brush_water_ice", $"height={Num(WaterHeight)}", c)),
                Check = (c, b, a) => (int)Unset(a[1]) == (int)CustomLiquidType.Ice ? null : $"the disk's type reads {Type(a[1])}, not Ice",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "waterfall", Points = WaterPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => Once(() => Line("va_brush_waterfall", "", c)),
                Check = (c, b, a) => AdminToolsCases.Expect(a[0], c.y + WaterfallAbove, "the waterfall sheet's surface"),
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "water_volume", Points = PourPoints, SettleSeconds = VolumeSettleSeconds,
                Apply = c => Once(() => Line("va_brush_water_volume", $"height={Num(WaterHeight)}", c)),
                Check = (c, b, a) => a[0] > c.y + PresentAboveGround ? null : $"no pour at the centre (level {a[0]:0.00}, ground {c.y:0.00})",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "water_spring", Points = PourPoints, SettleSeconds = SpringSettleSeconds,
                Apply = c => Once(() => Line("va_brush_water_spring", $"thickness=1 height={Num(WaterHeight)}", c)),
                Check = (c, b, a) => a[0] > c.y + PresentAboveGround ? null : $"no spring water at the source (level {a[0]:0.00})",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "water_freeze", Points = PourPoints, SettleSeconds = SpringSettleSeconds,
                Apply = c => Steps(
                    () => Line("va_brush_water_spring", $"thickness=1 height={Num(WaterHeight)}", c),
                    () => Line("va_brush_water_freeze", "", c)),
                Check = (c, b, a) => a[1] == 1f ? null : $"the spring is not frozen (reads {a[1]})",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "water_delete", Points = WaterPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => Steps(
                    () => Line("va_brush_water", $"height={Num(WaterHeight)}", c),
                    () => Line("va_brush_water_delete", "", c)),
                Check = (c, b, a) => float.IsNaN(a[0]) ? null : $"water still stands at the centre ({a[0]:0.00}) after the delete",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "water_mop", Points = PourPoints, SettleSeconds = VolumeSettleSeconds,
                Apply = c => Steps(
                    () => Line("va_brush_water_volume", $"height={Num(WaterHeight)}", c),
                    () => Line("va_brush_water_mop", "", c),
                    () => Line("va_brush_water_mop", "", c)),
                Check = (c, b, a) => a[0] <= c.y + WaterHeight * 0.5f ? null : $"the mop left the pour at {a[0]:0.00} (ground {c.y:0.00})",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "ice_land", Points = IcePoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => Once(() => Line("place_ice_land", "offset=0", c)),
                Check = (c, b, a) => AdminToolsCases.Expect(a[0], c.y + LandIceAbove, "the land ice's base"),
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "ice_land_raise", Points = IcePoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => Steps(
                    () => Line("place_ice_land", "offset=0", c),
                    () => Line("ice_land_raise", $"raise={Num(IceRaise)}", c)),
                Check = (c, b, a) => AdminToolsCases.Expect(a[3], c.y + LandIceAbove + IceRaise, "the raised ice's top (what you stand on)"),
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "ice_water_break", Points = IcePoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => Steps(
                    () => Line("va_brush_water", $"height={Num(WaterHeight)}", c),
                    () => Line("place_ice_water", "offset=0", c),
                    () => Line("break_standalone_ice", "", c)),
                Check = (c, b, a) => float.IsNaN(a[1]) ? "no floating ice over the water"
                    : a[2] >= 1f ? null : $"the break left {a[2]} hole(s) in the floating ice",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "snow_land", Points = SnowPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => Once(() => Line("place_land_snow", "offset=0", c)),
                Check = (c, b, a) => float.IsNaN(a[0]) ? "no land snow at the centre"
                    : a[0] - c.y >= SnowMinAbove && a[0] - c.y <= SnowMaxAbove ? null : $"the snow's surface is {a[0] - c.y:+0.00;-0.00} m over the ground",
            };
            foreach (var vtool in VToolCases()) yield return vtool;
        }

        // ── The Chaos Hammer's own water tools (0.2.715, the lead / Fire 10-05) ─────────────────────────────────────────
        // Driven through the calls the V-tools make (WaterShapingTools, VoxelWorld.MopWater, ToolFalls), like the voxel
        // family drives ApplyBrush; the "tools" family proves the real click path. Sized for the island's 7 m cells. The
        // place's persist_unload step re-reads every one of them after an unload, on this peer, the server and the bot.
        private const float VPoolRadius = 2f, VRiverRadius = 1.2f, VRiverHalf = 2.2f, VRiverDrop = 1f;
        private const float VPourAbove = 1f, VBowlDepth = 2f, VBowlRadius = 2.5f, VBowlMinDepth = 1f;
        private const float VMopRadius = 3f, VPitRadius = 1.5f, VPitDepth = 5f, VFallBrush = 1f;
        private const float VWaitForWater = 3f, VWaitForColliders = 2f, VPoolSettleSeconds = 20f;
        private const float VPondRadius = 3.5f, VPondBelow = 0.5f, VPondOver = 1f, VPondTolerance = 0.05f;

        private static IEnumerable<ToolCase> VToolCases()
        {
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vpour", Points = WaterPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => VTool(() => VerdantsAscent.Water.Tools.WaterShapingTools.PourDab(c + Vector3.up * VPourAbove, VPoolRadius)),
                Check = (c, b, a) => AdminToolsCases.Expect(a[0], c.y + VPourAbove, "the pour's surface"),
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vpool", Points = PoolPoints, SettleSeconds = VPoolSettleSeconds,
                Apply = c => VTool(() => VerdantsAscent.Water.Tools.WaterShapingTools.PoolClick(c, VPoolRadius)),
                Check = (c, b, a) => AdminToolsCases.Expect(a[0], c.y, "the pool's surface")
                    ?? (a[1] <= c.y - VBowlMinDepth ? null : $"the pool's bed reads {a[1]:0.00}, not carved under the surface {c.y:0.00}"),
            };
            // Fire: "carve and then fill a depression with water": a bowl cut first, then Vpool + LeftAlt fills it to its rim.
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vpool_fill", Points = PoolPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => VToolSteps(
                    () => VerdantsAscent.Terrain.Voxel.VoxelWorld.ApplyBrush(c + Vector3.down * (VBowlDepth * 0.5f), VBowlRadius, VBowlDepth,
                        VerdantsAscent.Terrain.Voxel.VoxelBrushShape.Cylinder, VerdantsAscent.Terrain.Voxel.VoxelBrushMode.Carve),
                    () => FiresLogger.LogDebug(VerdantsAscent.Water.Tools.WaterShapingTools.FillBowl(c + Vector3.down * VBowlDepth, VPoolRadius))),
                Check = (c, b, a) => float.IsNaN(a[0]) ? "no water in the carved bowl"
                    : a[0] > c.y + AdminToolsCases.HeightTolerance ? $"the water ({a[0]:0.00}) stands over the bowl's rim ({c.y:0.00})"
                    : a[0] - a[1] >= VBowlMinDepth ? null : $"the bowl is filled only {a[0] - a[1]:0.00} m deep (water {a[0]:0.00}, bed {a[1]:0.00})",
            };
            // 0.2.745 Vpond (Fire 10-05: "a radius flat circle ... adjust the fill height ... draw a water mesh in a depression"): a bowl
            // cut first, then a circle wider than the bowl at VPondBelow under the ground: the surface reads that height within
            // VPondTolerance (the lead: "a disc at height H gives a water surface of H +-0.05"; the harness holds the server and the
            // bot to the same reading).
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vpond", Points = PoolPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => VToolSteps(
                    () => VerdantsAscent.Terrain.Voxel.VoxelWorld.ApplyBrush(c + Vector3.down * (VBowlDepth * 0.5f), VBowlRadius, VBowlDepth,
                        VerdantsAscent.Terrain.Voxel.VoxelBrushShape.Cylinder, VerdantsAscent.Terrain.Voxel.VoxelBrushMode.Carve),
                    () => FiresLogger.LogDebug(VerdantsAscent.Water.Tools.PondTool.Fill(c, c.y - VPondBelow, VPondRadius))),
                Check = (c, b, a) => float.IsNaN(a[0]) ? $"no water in the bowl (wanted a surface at {c.y - VPondBelow:0.00})"
                    : Mathf.Abs(a[0] - (c.y - VPondBelow)) > VPondTolerance ? $"the pond's surface reads {a[0]:0.00}, not the circle's height {c.y - VPondBelow:0.00} (+-{VPondTolerance:0.00})"
                    : a[0] - a[1] >= VBowlMinDepth - VPondBelow ? null : $"the pond is only {a[0] - a[1]:0.00} m deep (water {a[0]:0.00}, bed {a[1]:0.00})",
            };
            // The same bowl with the circle set over the ground: the rim lies under the height all round, so nothing may be laid.
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vpond_spill", Points = PoolPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => VToolSteps(
                    () => VerdantsAscent.Terrain.Voxel.VoxelWorld.ApplyBrush(c + Vector3.down * (VBowlDepth * 0.5f), VBowlRadius, VBowlDepth,
                        VerdantsAscent.Terrain.Voxel.VoxelBrushShape.Cylinder, VerdantsAscent.Terrain.Voxel.VoxelBrushMode.Carve),
                    () => FiresLogger.LogDebug(VerdantsAscent.Water.Tools.PondTool.Fill(c, c.y + VPondOver, VPondRadius))),
                Check = (c, b, a) => float.IsNaN(a[0]) ? null : $"water was laid at {a[0]:0.00} although the circle at {c.y + VPondOver:0.00} spills over the rim",
            };
            // Fire: "click one water source and then click a second water source and have a river auto carve between them".
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vriver", Points = RiverPoints, SettleSeconds = VPoolSettleSeconds,
                Apply = c => VTool(() =>
                {
                    VerdantsAscent.Water.Tools.WaterShapingTools.PoolClick(c + Vector3.left * VRiverHalf, VRiverRadius);
                    VerdantsAscent.Water.Tools.WaterShapingTools.PoolClick(c + Vector3.right * VRiverHalf + Vector3.down * VRiverDrop, VRiverRadius);
                }),
                Check = (c, b, a) =>
                {
                    if (float.IsNaN(a[0]) || float.IsNaN(a[1]) || float.IsNaN(a[2]))
                        return $"the river is broken: water at A {Show(a[0])}, midway {Show(a[1])}, B {Show(a[2])}";
                    if (a[0] < a[1] - SurfaceTolerance || a[1] < a[2] - SurfaceTolerance)
                        return $"the river does not fall from A to B ({a[0]:0.00} -> {a[1]:0.00} -> {a[2]:0.00})";
                    if (a[0] - a[2] < VRiverDrop * 0.5f) return $"A ({a[0]:0.00}) is not above B ({a[2]:0.00}): no flow A -> B";
                    return a[3] <= a[1] - VBowlMinDepth * 0.5f ? null : $"no bed carved midway (ground {a[3]:0.00}, water {a[1]:0.00})";
                },
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vmop", Points = WaterPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => VToolWaitThen(
                    () => VerdantsAscent.Water.Tools.WaterShapingTools.PourDab(c + Vector3.up * VPourAbove, VPoolRadius),
                    () => Mop(c, false)),
                Check = (c, b, a) => float.IsNaN(a[0]) ? null : $"water still stands at the centre ({a[0]:0.00}) after the mop",
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vmop_restore", Points = WaterPoints, SettleSeconds = DiskSettleSeconds,
                Apply = c => VToolWaitThen(
                    () => VerdantsAscent.Water.Tools.WaterShapingTools.PourDab(c + Vector3.up * VPourAbove, VPoolRadius),
                    () => { Mop(c, false); Mop(c, true); }),
                Check = (c, b, a) => AdminToolsCases.Expect(a[0], c.y + VPourAbove, "the pour's surface after mop + restore"),
            };
            yield return new ToolCase
            {
                Family = WaterFamily, Name = "vfall", Points = FallPoints, SettleSeconds = VPoolSettleSeconds,
                Apply = c => VFall(c),
                Check = (c, b, a) => a[0] < 1f ? $"no fall at the lip (FallsNear {Show(a[0])})"
                    : float.IsNaN(a[1]) ? "no pool where the fall lands" : null,
            };
        }

        private static List<ProbePoint> PoolPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.WaterSurface, c),
            new ProbePoint(ProbeKind.VoxelSurface, c + Vector3.down * VBowlDepth),
        };

        private static List<ProbePoint> RiverPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.WaterSurface, c + Vector3.left * VRiverHalf),
            new ProbePoint(ProbeKind.WaterSurface, c + Vector3.down * (VRiverDrop * 0.5f)),
            new ProbePoint(ProbeKind.WaterSurface, c + Vector3.right * VRiverHalf + Vector3.down * VRiverDrop),
            new ProbePoint(ProbeKind.VoxelSurface, c + Vector3.down * VBowlDepth),
        };

        private static List<ProbePoint> FallPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.FallsNear, c),
            new ProbePoint(ProbeKind.WaterSurface, c + Vector3.right * VPitRadius + Vector3.down * VPitDepth),
        };

        private static string Show(float value) => float.IsNaN(value) ? "none" : value.ToString("0.00", CultureInfo.InvariantCulture);

        private static void Mop(Vector3 c, bool restore)
        {
            VerdantsAscent.Terrain.Voxel.VoxelWorld.MopWater(c, VMopRadius, restore, out _);
            WaterMeshCombiner.RefreshMopsIn(c, VMopRadius);
        }

        // Every V-tool case runs with no PgUp/PgDn offset and no pool chain left over from a player or another case.
        private static IEnumerator VTool(Action action) => VToolSteps(action);

        private static IEnumerator VToolSteps(params Action[] actions)
        {
            float offset = VerdantsAscent.Water.Tools.WaterShapingTools.LevelOffset;
            VerdantsAscent.Water.Tools.WaterShapingTools.LevelOffset = 0f;
            VerdantsAscent.Water.Tools.WaterShapingTools.ClearAnchor();
            try
            {
                foreach (var action in actions) action();
            }
            finally
            {
                VerdantsAscent.Water.Tools.WaterShapingTools.ClearAnchor();
                VerdantsAscent.Water.Tools.WaterShapingTools.LevelOffset = offset;
            }
            yield break;
        }

        // The first action's water has to exist (it is laid through the server) before the second can act on it.
        private static IEnumerator VToolWaitThen(Action first, Action then)
        {
            var one = VToolSteps(first);
            while (one.MoveNext()) yield return one.Current;
            yield return new WaitForSecondsRealtime(VWaitForWater);
            var two = VToolSteps(then);
            while (two.MoveNext()) yield return two.Current;
        }

        // A pit beside the cell's centre, then a Vfall click just short of its edge looking across it.
        private static IEnumerator VFall(Vector3 c)
        {
            VerdantsAscent.Terrain.Voxel.VoxelWorld.ApplyBrush(c + Vector3.right * VPitRadius + Vector3.down * (VPitDepth * 0.5f),
                VPitRadius, VPitDepth, VerdantsAscent.Terrain.Voxel.VoxelBrushShape.Cylinder, VerdantsAscent.Terrain.Voxel.VoxelBrushMode.Carve);
            yield return new WaitForSecondsRealtime(VWaitForColliders);   // the landing ray needs the pit's colliders
            var place = VToolSteps(() => FiresLogger.LogDebug(
                VerdantsAscent.Water.Tools.ToolFalls.Place(c + Vector3.left * VPitRadius, Vector3.right, VFallBrush)));
            while (place.MoveNext()) yield return place.Current;
        }

        private static ToolCase Disk(string name, string command, CustomLiquidType type) => new ToolCase
        {
            Family = WaterFamily, Name = name, Points = WaterPoints, SettleSeconds = DiskSettleSeconds,
            Apply = c => Once(() => Line(command, $"height={Num(WaterHeight)}", c)),
            Check = (c, b, a) => AdminToolsCases.Expect(a[0], c.y + WaterHeight, $"the {type} surface")
                                 ?? ((int)Unset(a[1]) == (int)type ? null : $"the disk's type reads {Type(a[1])}, not {type}"),
        };

        private static List<ProbePoint> WaterPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.WaterSurface, c),
            new ProbePoint(ProbeKind.WaterType, c),
            new ProbePoint(ProbeKind.Standing, c),
        };

        private static List<ProbePoint> PourPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.PourLevel, c),
            new ProbePoint(ProbeKind.SpringFrozen, c),
        };

        private static List<ProbePoint> IcePoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.LandIce, c),
            new ProbePoint(ProbeKind.FloatingIce, c),
            new ProbePoint(ProbeKind.FloatingIceHoles, c),
            new ProbePoint(ProbeKind.Standing, c),
        };

        private static List<ProbePoint> SnowPoints(Vector3 c) => new List<ProbePoint>
        {
            new ProbePoint(ProbeKind.LandSnow, c),
        };

        private static void Line(string command, string arguments, Vector3 at)
            => AdminToolsCases.RunConsoleLine(string.Format(CultureInfo.InvariantCulture, "{0} {1} from={2:0.##},{3:0.##},{4:0.##} circle={5:0.##}",
                command, arguments, at.x, at.z, at.y, Radius).Replace("  ", " "));

        private static float Unset(float value) => float.IsNaN(value) ? -1f : value;

        private static string Type(float value) => float.IsNaN(value) ? "none" : ((CustomLiquidType)(int)value).ToString();

        private static string Num(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static IEnumerator Once(Action action)
        {
            action();
            yield break;
        }

        private static IEnumerator Steps(params Action[] actions)
        {
            foreach (var action in actions)
            {
                action();
                yield return null;
            }
        }
    }
}
