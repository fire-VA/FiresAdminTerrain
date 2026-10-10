using System.Globalization;
using System.Text;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.847 (the lead, SinkTestGen8 river hump at (-503,-3563): 0.2.846a's spread fix changed nothing, byte-same sheets):
    // `fat_water_line [x z] [half] [step]` (read-only): a line ACROSS the river at a point (perpendicular to the generated flow),
    // and for each sample the generated water level and depth (GenWaterField: the bake's own water), the generated ground
    // (SampleFinalMeters), and the STORED column level the sheet is drawn from (VoxelWaterSurface.SurfaceAt). A crown in the
    // generated level is the bake's; a stored level over the generated one is ours (spread or record).
    internal static class FatWaterLine
    {
        private const float DefaultHalf = 16f, DefaultStep = 2f;

        internal static void Register()
        {
            new Terminal.ConsoleCommand("fat_water_line",
                "[x z] [half] [step] [along | dx dz] - water across (or along, or along dx dz) the river at a point: generated level/depth, generated ground, stored sheet level (read-only)",
                args =>
                {
                    var inv = CultureInfo.InvariantCulture;
                    var camera = global::Utils.GetMainCamera();
                    Vector3 c = camera != null ? camera.transform.position : Vector3.zero;
                    if (args.Length >= 3 && float.TryParse(args[1], NumberStyles.Float, inv, out float ax) && float.TryParse(args[2], NumberStyles.Float, inv, out float az))
                        c = new Vector3(ax, c.y, az);
                    float half = args.Length >= 4 && float.TryParse(args[3], NumberStyles.Float, inv, out float h) ? h : DefaultHalf;
                    float step = args.Length >= 5 && float.TryParse(args[4], NumberStyles.Float, inv, out float s) ? Mathf.Max(0.25f, s) : DefaultStep;
                    Vector2 flow = FiresWorldGenBase.GenFlowAt(c.x, c.z);
                    Vector2 across = flow.sqrMagnitude > 1e-6f ? new Vector2(-flow.y, flow.x).normalized : Vector2.right;
                    if (args.Length >= 6 && args[5] == "along" && flow.sqrMagnitude > 1e-6f) across = flow.normalized;   // the line along the flow instead
                    // 0.2.851: an explicit line direction "dx dz" (the flow can be ~0 on flat water, so "along" is not repeatable).
                    if (args.Length >= 7 && float.TryParse(args[5], NumberStyles.Float, inv, out float ldx) && float.TryParse(args[6], NumberStyles.Float, inv, out float ldz) && ldx * ldx + ldz * ldz > 1e-6f)
                        across = new Vector2(ldx, ldz).normalized;
                    var sb = new StringBuilder();
                    sb.Append(string.Format(inv, "[WaterLine] at ({0:0.0},{1:0.0}) flow ({2:0.00},{3:0.00}), across ({4:0.00},{5:0.00}); offset: gen level / gen depth / gen ground / STORED level / stored - gen",
                        c.x, c.z, flow.x, flow.y, across.x, across.y));
                    for (float o = -half; o <= half + 0.001f; o += step)
                    {
                        float x = c.x + across.x * o, z = c.z + across.y * o;
                        bool gen = GenWaterField.TryWaterSurfaceSmooth(x, z, out float lvl, out float dep);
                        float ground = FiresWorldGenBase.SampleFinalMeters(x, z);
                        float stored = VoxelWaterSurface.SurfaceAt(new Vector3(x, (gen ? lvl : ground) + 0.5f, z));
                        sb.Append(string.Format(inv, "\n  {0,6:+0.0;-0.0}: {1} / {2} / {3:0.00} / {4} / {5}{6}",
                            o, gen ? lvl.ToString("0.00", inv) : "-", gen ? dep.ToString("0.00", inv) : "-", ground,
                            stored > -1000f ? stored.ToString("0.00", inv) : "-",
                            gen && stored > -1000f ? (stored - lvl).ToString("+0.00;-0.00", inv) : "-",
                            FiresWorldGenBase.DescribePlanWaterAt(x, z)));   // 0.2.848: the plan's body / run here
                    }
                    string line = sb.ToString();
                    FiresLogger.LogInfo(line);
                    global::Console.instance?.Print(line);
                });
        }
    }
}

namespace VerdantsAscent.Terrain.Voxel
{
    public static partial class FiresWorldGenBase
    {
        // 0.2.848 (fat_water_line): which plan water owns a point - the still body (kind, id, level) and the river run (id,
        // level) - so a mound in the generated level can be traced to a body or a run.
        internal static string DescribePlanWaterAt(float x, float z)
        {
            var plan = _plan;
            if (plan == null) return " | no plan";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string body = plan.TryGetBodyAt(x, z, out var b) && b != null ? string.Format(inv, "body {0} #{1} at {2:0.00}", b.Kind, b.Id, b.Level) : "no body";
            string run = plan.TryGetRiverAt(x, z, 0f, out float rl, out float rw, out int id) ? string.Format(inv, "run #{0} at {1:0.00} (w {2:0.0})", id, rl, rw) : "no run";
            return " | " + body + " | " + run;
        }
    }
}
