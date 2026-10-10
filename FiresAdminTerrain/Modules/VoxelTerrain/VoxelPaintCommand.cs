using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{

    // 0.2.857 ([visual], FDT 1.1.410; Fire: the levelled test pad is PAINTED DIRT with no grass): `va_voxel_paint <x> <z> <radius>
    // [dirt|paved|cultivate|reset]` paints a disc on the GROUND through vanilla's own paint operation (FiresUnifiedPaint.MirrorPaint:
    // a paint-only TerrainOp on every heightmap's TerrainComp the disc touches, as the hoe and cultivator do). It persists with
    // the zone's TerrainComp and the grass honours it (dirt and paved are "cleared": no grass). Logs
    // "[FiresVoxel] paint pad at (x,z) r R: <type> on N terrain comp(s)".
    internal static class VoxelPaintCommand
    {
        internal const string Command = "va_voxel_paint";

        internal static void Register()
        {
            new Terminal.ConsoleCommand(Command,
                "<x> <z> <radius> [dirt|paved|cultivate|reset] - paint a disc of the ground (vanilla paint: persists, dirt/paved clear the grass) (admin)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    if (args.Args.Length < 4
                        || !float.TryParse(args[1], NumberStyles.Float, inv, out float x)
                        || !float.TryParse(args[2], NumberStyles.Float, inv, out float z)
                        || !float.TryParse(args[3], NumberStyles.Float, inv, out float r) || r <= 0f)
                    {
                        args.Context?.AddString($"{Command} <x> <z> <radius> [dirt|paved|cultivate|reset]");
                        return;
                    }
                    string name = args.Args.Length >= 5 ? args[4].ToLowerInvariant() : "dirt";
                    TerrainModifier.PaintType type;
                    switch (name)
                    {
                        case "dirt": type = TerrainModifier.PaintType.Dirt; break;
                        case "paved": type = TerrainModifier.PaintType.Paved; break;
                        case "cultivate": case "cultivated": type = TerrainModifier.PaintType.Cultivate; break;
                        case "reset": type = TerrainModifier.PaintType.Reset; break;
                        default: args.Context?.AddString($"{Command}: paint must be dirt, paved, cultivate or reset"); return;
                    }
                    float y = FiresWorldGenBase.Active ? FiresWorldGenBase.SampleFinalMeters(x, z) : (ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(new Vector3(x, 1000f, z)) : 0f);
                    if (VoxelWorld.TrySampleSurfaceY(x, z, y + 20f, y - 20f, out float top, out bool wall) && !wall) y = top;
                    int comps = VerdantsAscent.Terrain.Paint.FiresUnifiedPaint.MirrorPaint(new Vector3(x, y, z), r, type, false);
                    string line = string.Format(inv, "[FiresVoxel] paint pad at ({0:0.0},{1:0.0}) r {2:0.0}: {3} on {4} terrain comp(s)", x, z, r, name, comps);
                    FiresLogger.LogInfo(line);
                    args.Context?.AddString(line);
                }, isCheat: true);
        }
    }
}
