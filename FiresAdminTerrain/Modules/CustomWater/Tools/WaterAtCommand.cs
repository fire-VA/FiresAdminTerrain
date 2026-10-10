using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using VerdantsAscent.Water.Core;

namespace VerdantsAscent.Water.Tools
{
    // `va_water_at [x z [y]]` (0.2.776; overnight Q1 swim_test: Floating.GetWaterLevel read 44.91 over a sea whose surface was about 25,
    // so something would float boats, items and players 20 m in the air there). Floating.GetWaterLevel is what the game floats things
    // with, and FAT raises it to the highest custom water over the point (CustomWaterPatches.RaiseToPourSurface and
    // WaterBoatSupport.RaiseToCustomWater). This lists every source's answer at the point: the vanilla water volume, each placed-water
    // combiner, each legacy water box, each pour, the generated voxel water, and the final GetWaterLevel. Without arguments it asks at
    // the local player. Describe() is the same report as one line for tests (swim_test reads it by reflection).
    internal static class WaterAtCommand
    {
        internal const string Command = "va_water_at";
        private const int ListMax = 12;
        private static bool s_registered;

        internal static void RegisterCommand()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(Command,
                "[x z [y]] - every water source's surface at the point (default: where you stand): what Floating.GetWaterLevel floats things on.",
                args =>
                {
                    Vector3 p;
                    var player = Player.m_localPlayer;
                    if (args.Length >= 3 && TryF(args[1], out float x) && TryF(args[2], out float z))
                        p = new Vector3(x, args.Length >= 4 && TryF(args[3], out float y) ? y : (player != null ? player.transform.position.y : 30f), z);
                    else if (player != null) p = player.transform.position;
                    else { args.Context?.AddString(Command + ": join a world or give x z [y]."); return; }
                    string line = Describe(p);
                    FiresLogger.LogInfo("[FiresWater] " + line);
                    foreach (var part in line.Split(new[] { " | " }, System.StringSplitOptions.None)) args.Context?.AddString(part);
                });
        }

        // One line: "water at (x,y,z): final F | volume V | combiner ... | pour ... | generated G".
        public static string Describe(Vector3 p)
        {
            var sb = new StringBuilder();
            sb.Append(F("water at ({0:0.0},{1:0.00},{2:0.0})", p.x, p.y, p.z));
            WaterVolume volume = null;
            float final = Floating.GetWaterLevel(p, ref volume);
            sb.Append(F(": final GetWaterLevel {0}", Y(final)));
            if (volume != null)
            {
                float vs = float.MinValue;
                try { vs = volume.GetWaterSurface(p, 1f); } catch { }
                sb.Append(F(" | volume '{0}' at y {1:0.00}: surface {2}", volume.name, volume.transform.position.y, Y(vs)));
            }
            else sb.Append(" | no water volume");
            if (ZoneSystem.instance != null) sb.Append(F(" | zone sea level {0:0.00}", ZoneSystem.instance.m_waterLevel));

            int n = 0, quiet = 0;
            foreach (var c in WaterMeshCombiner.AllCombiners)
            {
                if (c == null) continue;
                float s = c.GetWaterSurfaceAt(p);
                if (s <= float.MinValue) { quiet++; continue; }
                if (n++ >= ListMax) continue;
                var b = c.GetCombinedBounds();
                sb.Append(F(" | combiner '{0}' ({1}, {2} disk(s), bounds {3:0.0},{4:0.0}..{5:0.0},{6:0.0} y {7:0.0}..{8:0.0}): {9}",
                    c.name, c.CustomLiquidType, c.WaterDisks.Count, b.min.x, b.min.z, b.max.x, b.max.z, b.min.y, b.max.y, Y(s)));
                var d = c.WaterDisks.Where(w => w != null).OrderBy(w => (new Vector2(w.Position.x - p.x, w.Position.z - p.z)).sqrMagnitude).FirstOrDefault();
                if (d != null) sb.Append(F(" nearest disk ({0:0.0},{1:0.00},{2:0.0}) r {3:0.0} depth {4:0.0}", d.Position.x, d.Position.y, d.Position.z, d.Radius, d.MaxDepth));
            }
            sb.Append(F(" | {0} combiner(s) claim nothing here", quiet));

            n = 0; quiet = 0;
            foreach (var w in CustomWaterManager.AllWater)
            {
                if (w == null) continue;
                float s = w.GetWaterSurfaceAt(p);
                if (s <= float.MinValue || s <= -9999f) { quiet++; continue; }
                if (n++ < ListMax) sb.Append(F(" | water box '{0}' at ({1:0.0},{2:0.0},{3:0.0}): {4}", w.name, w.transform.position.x, w.transform.position.y, w.transform.position.z, Y(s)));
            }
            if (quiet > 0) sb.Append(F(" | {0} water box(es) claim nothing", quiet));

            n = 0; quiet = 0;
            foreach (var lv in FiresLiquidWaterPrefab.ActivePours)
            {
                if (lv == null) continue;
                var lp = lv.transform.position;
                float half = lv.m_width * lv.m_scale * 0.5f;
                if (Mathf.Abs(p.x - lp.x) > half || Mathf.Abs(p.z - lp.z) > half) { quiet++; continue; }
                float s = float.MinValue;
                try { s = lv.GetSurface(p); } catch { }
                // 0.2.781 (the lead 10-06: one pour listed 4+ times, its raw 53.74 shown): each volume names itself (instance and ZDO
                // id, so stacked copies of one pour show as such) and says what the game reads there: its surface where the cell
                // holds liquid, "dry" (the game reads none since 0.2.780) with the raw bed answer beside it.
                var nview = lv.GetComponentInParent<ZNetView>();
                string zdo = nview != null && nview.GetZDO() != null ? nview.GetZDO().m_uid.ToString() : "no ZDO";
                bool wet = FiresLiquidWaterPrefab.PourCellWet(lv, p);
                if (n++ < ListMax) sb.Append(F(" | pour '{0}' #{1} (ZDO {2}) at ({3:0.0},{4:0.00},{5:0.0}) {6:0} m square: {7}",
                    lv.name, lv.GetInstanceID(), zdo, lp.x, lp.y, lp.z, half * 2f, wet ? Y(s) : "dry, the game reads none (raw " + Y(s) + ")"));
            }
            sb.Append(F(" | pours: {0} over the point (by footprint), {1} elsewhere; FAT's pour raise says {2}", n, quiet, Y(FiresLiquidWaterPrefab.GetSurfaceAt(p))));

            float gen = float.MinValue;
            try { gen = VerdantsAscent.Terrain.Voxel.VoxelWaterSurface.SurfaceAt(p); } catch { }
            sb.Append(F(" | generated voxel water {0}", Y(gen)));
            float custom = float.MinValue;
            try { custom = CustomWaterManager.GetWaterLevelAt(p); } catch { }
            sb.Append(F(" | CustomWaterManager max {0}", Y(custom)));
            return sb.ToString();
        }

        private static string Y(float v) => v <= -9999f || v <= float.MinValue ? "none" : v.ToString("0.00", CultureInfo.InvariantCulture);
        private static bool TryF(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        private static string F(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
