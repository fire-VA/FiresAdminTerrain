using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── `va_site_scan [radius=400] [top=10] [x z]`: where to put a base near spawn (Fire, 09-30: the co-op stream's base is
    // "auto-picked near spawn: flat, safe, trees and water nearby") ────────────────────────────────────────────────────────
    // Read-only and admin-only: it only asks vanilla's WorldGenerator and the vegetation rules, writes nothing and places
    // nothing. Candidates on a 10 m grid round the centre (default: where the admin stands, i.e. the spawn on a first login),
    // each judged on:
    //   ground   relief over a 30 x 30 m square (3 m samples) and its lowest point above the water level
    //   biome    Meadows at the centre and at the square's corners
    //   water    the nearest generated water (ground below the water level) within 80 m
    //   trees    how many of 25 points within 40 m a vanilla tree rule (a vegetation entry whose prefab is a TreeBase) allows:
    //            biome, altitude and forest factor, as ZoneSystem.PlaceVegetation tests them. It says where trees CAN grow,
    //            not where each one stands (placement is random per zone).
    //   spawn    distance from the centre (nearer is better, but not within 40 m of it: the StartTemple stands there)
    // Locations (camps, spawner runestones, dungeons) live only on the server, so they are not judged here: on a client the
    // list is the worldgen half, and the top spots are checked against the saved location list offline. One line per spot:
    //   [FiresSiteScan] #k (x, z) ground y LO-HI (relief R m), Meadows, water D m, trees k/25, D m from the centre
    internal static class SiteScanCommand
    {
        private const string Command = "va_site_scan";
        private const float Grid = 10f, Half = 15f, Sample = 3f, WaterReach = 80f, TreeReach = 40f, CentreKeepOut = 40f;
        private const float MaxRelief = 3f, MinAboveWater = 1f;

        internal static void Register()
        {
            new Terminal.ConsoleCommand(Command, "[radius=400] [top=10] [x z] - rank flat, dry Meadows base spots near spawn with water and trees nearby (admin, read-only)",
                args =>
                {
                    if (!FiresCore.Sync.AdminSyncing.IsLocalAdmin()) { args.Context.AddString($"{Command} refused: not an admin"); return; }
                    var gen = WorldGenerator.instance;
                    var zs = ZoneSystem.instance;
                    if (gen == null || zs == null) { args.Context.AddString($"{Command} refused: no world generator"); return; }
                    var inv = CultureInfo.InvariantCulture;
                    var style = NumberStyles.Float;
                    float radius = 400f; int top = 10;
                    if (args.Args.Length > 1) float.TryParse(args.Args[1], style, inv, out radius);
                    if (args.Args.Length > 2) int.TryParse(args.Args[2], out top);
                    Vector3 centre = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    if (args.Args.Length > 4 && float.TryParse(args.Args[3], style, inv, out float cx) && float.TryParse(args.Args[4], style, inv, out float cz))
                        centre = new Vector3(cx, 0f, cz);
                    radius = Mathf.Clamp(radius, 50f, 1500f);
                    top = Mathf.Clamp(top, 1, 30);
                    foreach (string line in Scan(gen, zs, centre, radius, top)) { FiresLogger.LogInfo(line); args.Context.AddString(line); }
                });
        }

        private struct Spot { public float X, Z, Lo, Hi, Water, Dist, Score; public int Trees; }

        private static List<string> Scan(WorldGenerator gen, ZoneSystem zs, Vector3 centre, float radius, int top)
        {
            float water = zs.m_waterLevel;
            var trees = new List<ZoneSystem.ZoneVegetation>();
            foreach (var veg in zs.m_vegetation)
                if (veg != null && veg.m_enable && veg.m_max > 0f && veg.m_prefab != null && veg.m_prefab.GetComponent<TreeBase>() != null
                    && (veg.m_biome & Heightmap.Biome.Meadows) != 0)
                    trees.Add(veg);

            var spots = new List<Spot>();
            int judged = 0;
            for (float z = centre.z - radius; z <= centre.z + radius; z += Grid)
                for (float x = centre.x - radius; x <= centre.x + radius; x += Grid)
                {
                    float dist = new Vector2(x - centre.x, z - centre.z).magnitude;
                    if (dist > radius || dist < CentreKeepOut) continue;
                    judged++;
                    if (!Meadows(gen, x, z) || !Meadows(gen, x - Half, z - Half) || !Meadows(gen, x + Half, z - Half)
                        || !Meadows(gen, x - Half, z + Half) || !Meadows(gen, x + Half, z + Half)) continue;
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (float dz = -Half; dz <= Half; dz += Sample)
                        for (float dx = -Half; dx <= Half; dx += Sample)
                        {
                            float h = gen.GetHeight(x + dx, z + dz);
                            lo = Mathf.Min(lo, h); hi = Mathf.Max(hi, h);
                        }
                    if (hi - lo > MaxRelief || lo < water + MinAboveWater) continue;
                    float near = NearestWater(gen, x, z, water);
                    int t = TreePoints(gen, trees, x, z, water);
                    // Flat first, then water and trees near, then near spawn.
                    float score = (hi - lo) * 2f + (near > WaterReach ? 6f : near / 20f) + (25 - t) * 0.15f + dist / 200f;
                    spots.Add(new Spot { X = x, Z = z, Lo = lo, Hi = hi, Water = near, Trees = t, Dist = dist, Score = score });
                }
            spots.Sort((a, b) => a.Score.CompareTo(b.Score));

            var lines = new List<string>();
            var picked = new List<Spot>();
            foreach (var s in spots)
            {
                if (picked.Count >= top) break;
                bool close = false;
                foreach (var p in picked) if (new Vector2(p.X - s.X, p.Z - s.Z).magnitude < 60f) { close = true; break; }
                if (close) continue;
                picked.Add(s);
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "[FiresSiteScan] #{0} ({1:0}, {2:0}) ground y {3:0.0}-{4:0.0} (relief {5:0.0} m), Meadows, water {6}, trees {7}/25 within {8:0} m, {9:0} m from the centre",
                    picked.Count, s.X, s.Z, s.Lo, s.Hi, s.Hi - s.Lo, s.Water > WaterReach ? $"none within {WaterReach:0} m" : $"{s.Water:0} m", s.Trees, TreeReach, s.Dist));
            }
            lines.Insert(0, string.Format(CultureInfo.InvariantCulture,
                "[FiresSiteScan] centre ({0:0}, {1:0}), radius {2:0} m: {3} spot(s) judged, {4} flat dry Meadows ({5} tree rule(s)); top {6}. Locations (camps, spawners, dungeons) are NOT checked here.",
                centre.x, centre.z, radius, judged, spots.Count, trees.Count, picked.Count));
            return lines;
        }

        private static bool Meadows(WorldGenerator gen, float x, float z) => gen.GetBiome(x, z) == Heightmap.Biome.Meadows;

        private static float NearestWater(WorldGenerator gen, float x, float z, float water)
        {
            for (float r = 10f; r <= WaterReach; r += 10f)
                for (int a = 0; a < 24; a++)
                {
                    float ang = a * Mathf.PI * 2f / 24f;
                    if (gen.GetHeight(x + Mathf.Cos(ang) * r, z + Mathf.Sin(ang) * r) < water) return r;
                }
            return float.MaxValue;
        }

        // Of 25 points within TreeReach (a 5 x 5 grid), how many at least one Meadows tree rule allows (biome, altitude, forest).
        private static int TreePoints(WorldGenerator gen, List<ZoneSystem.ZoneVegetation> trees, float x, float z, float water)
        {
            int ok = 0;
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                {
                    float px = x - TreeReach + i * TreeReach / 2f, pz = z - TreeReach + j * TreeReach / 2f;
                    if (gen.GetBiome(px, pz) != Heightmap.Biome.Meadows) continue;
                    float alt = gen.GetHeight(px, pz) - water;
                    var p = new Vector3(px, 0f, pz);
                    foreach (var veg in trees)
                    {
                        if (alt < veg.m_minAltitude || alt > veg.m_maxAltitude) continue;
                        if (veg.m_inForest)
                        {
                            float f = WorldGenerator.GetForestFactor(p);
                            if (f < veg.m_forestTresholdMin || f > veg.m_forestTresholdMax) continue;
                        }
                        ok++;
                        break;
                    }
                }
            return ok;
        }
    }
}
