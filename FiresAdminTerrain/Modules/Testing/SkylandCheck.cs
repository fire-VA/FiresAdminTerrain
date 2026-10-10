using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Testing
{
    // skyland_check (FAT 0.2.740; Fire 10-05: skylands reshape on approach, locations off their pads, dressing floating or
    // falling). Read-only, on the tester's client, for every skyland within Range of the player (stand on or beside one):
    //   shape     far view vs near voxels: the far top (what the far mesh is built from) against the live voxel surface,
    //             over every loaded column of a Step grid; PASS when the largest difference is within ShapeTolerance
    //   dressing  every object the skyland code placed (its dress tag) against the voxel surface under it; PASS when none
    //             stands more than FloatTolerance above it, and none is over a column with no island under it
    //   pad       the location pad's voxel surface on rings inside its flat radius; PASS when its slope stays under PadSlope
    // One line per skyland per check, then END.  skyland_check   (no args)
    internal static class SkylandCheck
    {
        private const string Command = "skyland_check";
        private const string Tag = "[SkylandCheck]";
        private const string ModVersion = global::VerdantsAscent.FiresAdminTerrain.PluginVersion;
        private const float Range = 300f, Step = 2f, ShapeTolerance = 0.6f, FloatTolerance = 0.3f, PadSlope = 6f, ProbeAbove = 40f, ProbeBelow = 60f;
        private static bool s_registered;
        private static readonly List<uint> s_ids = new List<uint>();
        private static readonly List<int> s_indices = new List<int>();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");

        internal static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(Command,
                "[F5 test] Skylands near you: far shape vs near voxels, floating dressing, location pad slope. Read-only.",
                args => Run());
        }

        private static void Run()
        {
            var me = Player.m_localPlayer;
            if (me == null || ZDOMan.instance == null) { Say($"{Tag} join a world first."); return; }
            FiresSkyIslands.SkylandsNear(me.transform.position, Range, s_ids, s_indices);
            int fails = 0, checks = 0;
            Say($"{Tag} BEGIN {ModVersion} ({FatDllHash.Value}): {s_indices.Count} skyland(s) within {Range:0} m");
            for (int i = 0; i < s_indices.Count; i++)
            {
                int index = s_indices[i];
                uint id = s_ids[i];
                checks += 3;
                fails += Shape(index, id) ? 0 : 1;
                fails += Dressing(index, id) ? 0 : 1;
                fails += Pad(index, id) ? 0 : 1;
            }
            Say($"{Tag} END {(fails > 0 ? "FAIL" : "PASS")}: {checks - fails} pass, {fails} fail");
        }

        private static bool Shape(int index, uint id)
        {
            var c = FiresSkyIslands.SkylandCentre(index);
            float r = FiresSkyIslands.SkylandRadius(index);
            int loaded = 0;
            float worst = 0f;
            Vector2 at = default;
            for (float dz = -r; dz <= r; dz += Step)
                for (float dx = -r; dx <= r; dx += Step)
                {
                    float x = c.x + dx, z = c.y + dz;
                    if (!FiresSkyIslands.TryFarTop(index, x, z, out float far)) continue;
                    if (!VoxelWorld.TrySampleSurfaceY(x, z, far + ProbeAbove * 0.25f, far - ProbeAbove * 0.25f, out float near, out bool wall) || wall) continue;
                    loaded++;
                    float diff = Mathf.Abs(near - far);
                    if (diff > worst) { worst = diff; at = new Vector2(x, z); }
                }
            bool pass = loaded > 0 && worst <= ShapeTolerance;
            Say(string.Format(CultureInfo.InvariantCulture, "{0} skyland {1} shape {2} - far top vs near voxels over {3} loaded column(s): largest difference {4:0.00} m{5} (limit {6:0.0})",
                Tag, id, loaded == 0 ? "SKIP" : pass ? "PASS" : "FAIL", loaded, worst, loaded > 0 ? $" at ({at.x:0},{at.y:0})" : "", ShapeTolerance));
            return pass || loaded == 0;
        }

        private static bool Dressing(int index, uint id)
        {
            long tag = (long)id + 1L;
            int count = 0, floating = 0, offIsland = 0, unread = 0;
            float worst = 0f;
            string worstName = null;
            foreach (var zdo in ObjectsById(ZDOMan.instance).Values)
            {
                if (zdo == null || zdo.GetLong(Zones.ZoneModeSkylands.DressTag) != tag) continue;
                count++;
                Vector3 p = zdo.GetPosition();
                if (!FiresSkyIslands.TryFarTop(index, p.x, p.z, out _)) { offIsland++; continue; }
                if (!VoxelWorld.TrySampleSurfaceY(p.x, p.z, p.y + 0.5f, p.y - ProbeBelow, out float ground, out _)) { unread++; continue; }
                float gap = p.y - ground;
                if (gap <= FloatTolerance) continue;
                floating++;
                if (gap > worst) { worst = gap; worstName = $"{Name(zdo)} at ({p.x:0},{p.y:0},{p.z:0})"; }
            }
            bool pass = floating == 0 && offIsland == 0;
            Say(string.Format(CultureInfo.InvariantCulture, "{0} skyland {1} dressing {2} - {3} tagged object(s): {4} over {5:0.0} m in the air{6}, {7} over no island, {8} over unloaded ground",
                Tag, id, count == 0 ? "SKIP" : pass ? "PASS" : "FAIL", count, floating, FloatTolerance, worstName != null ? $" (worst {worstName} {worst:0.00} m)" : "", offIsland, unread));
            return pass;
        }

        private static bool Pad(int index, uint id)
        {
            if (!FiresSkyIslands.TryGetSitePad(index, out Vector3 centre, out float flat))
            {
                Say($"{Tag} skyland {id} pad SKIP - no location");
                return true;
            }
            var heights = new List<Vector3>();
            for (float ring = 0f; ring <= flat * 0.9f; ring += Mathf.Max(1f, flat * 0.3f))
            {
                int around = ring <= 0f ? 1 : 12;
                for (int k = 0; k < around; k++)
                {
                    float a = k * Mathf.PI * 2f / around;
                    float x = centre.x + Mathf.Cos(a) * ring, z = centre.z + Mathf.Sin(a) * ring;
                    if (VoxelWorld.TrySampleSurfaceY(x, z, centre.y + 6f, centre.y - 12f, out float y, out _)) heights.Add(new Vector3(x, y, z));
                }
            }
            if (heights.Count < 3)
            {
                Say($"{Tag} skyland {id} pad SKIP - not loaded ({heights.Count} reading(s))");
                return true;
            }
            float worst = 0f, lowest = float.MaxValue, highest = float.MinValue;
            foreach (var h in heights) { lowest = Mathf.Min(lowest, h.y); highest = Mathf.Max(highest, h.y); }
            for (int i = 0; i < heights.Count; i++)
                for (int j = i + 1; j < heights.Count; j++)
                {
                    float run = new Vector2(heights[i].x - heights[j].x, heights[i].z - heights[j].z).magnitude;
                    if (run < 1f) continue;
                    worst = Mathf.Max(worst, Mathf.Atan2(Mathf.Abs(heights[i].y - heights[j].y), run) * Mathf.Rad2Deg);
                }
            bool pass = worst <= PadSlope;
            Say(string.Format(CultureInfo.InvariantCulture, "{0} skyland {1} pad {2} - flat radius {3:0.0} m at y {4:0.00}: {5} reading(s) {6:0.00}-{7:0.00}, steepest {8:0.0} deg (limit {9:0})",
                Tag, id, pass ? "PASS" : "FAIL", flat, centre.y, heights.Count, lowest, highest, worst, PadSlope));
            return pass;
        }

        private static string Name(ZDO zdo)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            return prefab != null ? prefab.name : "#" + zdo.GetPrefab().ToString(CultureInfo.InvariantCulture);
        }

        private static void Say(string line)
        {
            global::VerdantsAscent.FiresAdminTerrain.Log?.LogMessage(line);
            if (global::Console.instance != null) global::Console.instance.Print(line);
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresTests");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, Command + ".txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
