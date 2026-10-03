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
