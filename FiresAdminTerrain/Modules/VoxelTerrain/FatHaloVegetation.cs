using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // HALO RING VEGETATION (0.2.1042, step 4 of the ring biome plan; Fire 10-24: biome patches with trees and "the full
    // design", all biomes). Vanilla places vegetation per 64 m zone with rays straight down onto a heightfield, which can never
    // reach a ring wall. On a ring-only world the SERVER places vanilla's own vegetation table on the ring instead:
    //   * ring "zones" are CellSize-metre cells in the ring's unrolled (arc, axis) coordinates, wrapping round the ring;
    //   * a cell is placed once, when a player is within FocusCells of it and its floor collider exists, and is recorded in
    //     FiresWorldGen\firesgen_<world>.halozones so it is never placed twice (the objects are ordinary saved ZDOs);
    //   * per vanilla ZoneVegetation entry of the cell's ring biome (FatHaloBiome): vanilla's count rule (m_max < 1 = a
    //     chance, else m_min..m_max), groups (m_groupSizeMin..Max within m_groupRadius), scale, random spin, its tilt limits
    //     against the ring's up, and m_groundOffset - deterministic from the world seed, cell and prefab like vanilla;
    //   * every object is seated by a ray along the ring's down and stood up along the ring's up.
    // Skipped vanilla checks: altitude, ocean depth, forest factor, terrain delta and surround checks (the ring is flat and
    // has no heightfield to measure them on).
    internal static class FatHaloVegetation
    {
        private const float CellSize = 64f, RayLift = 3f, RayReach = 8f, TickSeconds = 1f;
        private const int FocusCells = 2, MaxCellsPerTick = 2;

        private static readonly HashSet<long> s_placed = new HashSet<long>();
        private static string s_loadedFor;
        private static float s_nextTick;
        private static int s_mask, s_cellsPlaced, s_objectsPlaced;

        // 0.2.1044 (Fire: "the veg to stop sliding off the world"): vanilla's StaticPhysics (m_fall / m_pushUp on trees, bushes,
        // rocks) settles an object along WORLD -Y onto GetSolidHeight/GetGroundHeight; on the ring wall world-down runs along the
        // wall, so everything slid. Inside the ring's field on a ring-only world it stands down: the object stays where the ring
        // placed it (seated along the ring's up).
        [HarmonyLib.HarmonyPatch(typeof(StaticPhysics), nameof(StaticPhysics.SUpdate))]
        private static class RingStaticPhysics
        {
            private static bool s_logged;

            private static bool Prefix(StaticPhysics __instance)
            {
                if (!FatHaloArc.RingOnly || !FatHaloArc.Active || __instance == null || !FatHaloArc.TryGetUp(__instance.transform.position, out _)) return true;
                if (!s_logged) { s_logged = true; FiresLogger.LogDebug($"[FatHalo] ring vegetation: vanilla StaticPhysics stands down on the ring (first '{__instance.name}')"); }
                return false;
            }
        }

        private static long Key(int cu, int cv) => ((long)cu << 32) ^ (uint)cv;

        private static string PathFor(string world) => FiresWorldRecords.PathFor(world, ".halozones");

        internal static void Tick()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null || ZNetScene.instance == null) return;
            if (!FatHaloArc.RingOnly || !FatHaloArc.Active || Time.unscaledTime < s_nextTick) return;
            s_nextTick = Time.unscaledTime + TickSeconds;
            string world = ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(world)) return;
            try
            {
                if (s_loadedFor != world) Load(world);
                if (!FatHaloArc.Axis(out Vector3 o, out Vector3 d, out float r) || !FatHaloArc.Band(out float w, out _, out _)) return;
                Vector3 ref0 = Mathf.Abs(d.y) > 0.999f ? Vector3.right : Vector3.ProjectOnPlane(Vector3.down, d).normalized;
                Vector3 ref1 = Vector3.Cross(d, ref0);
                int around = Mathf.Max(3, Mathf.RoundToInt(2f * Mathf.PI * r / CellSize));
                var focus = new List<Vector3>();
                if (Player.m_localPlayer != null) focus.Add(Player.m_localPlayer.transform.position);
                foreach (var peer in ZNet.instance.GetPeers()) if (peer != null) focus.Add(peer.m_refPos);
                int done = 0;
                foreach (var f in focus)
                {
                    if (!FatHaloArc.TryGetUp(f, out _)) continue;
                    Vector3 rel = f - o;
                    float v = Vector3.Dot(rel, d);
                    Vector3 radial = rel - v * d;
                    float ang = Mathf.Atan2(Vector3.Dot(radial, ref1), Vector3.Dot(radial, ref0));
                    float u = (ang < 0f ? ang + 2f * Mathf.PI : ang) * r;
                    int cu0 = Mathf.FloorToInt(u / CellSize), cv0 = Mathf.FloorToInt(v / CellSize);
                    for (int dv = -FocusCells; dv <= FocusCells && done < MaxCellsPerTick; dv++)
                        for (int du = -FocusCells; du <= FocusCells && done < MaxCellsPerTick; du++)
                        {
                            int cu = (((cu0 + du) % around) + around) % around, cv = cv0 + dv;
                            if ((cv + 1) * CellSize < -w || cv * CellSize > w) continue;   // off the band
                            if (s_placed.Contains(Key(cu, cv))) continue;
                            if (PlaceCell(world, cu, cv, around, o, d, r, w, ref0, ref1)) done++;
                        }
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatHalo] ring vegetation tick threw: {ex.Message}"); }
        }

        private static void Load(string world)
        {
            s_loadedFor = world;
            s_placed.Clear();
            try
            {
                string path = PathFor(world);
                if (System.IO.File.Exists(path))
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var p = line.Split(',');
                        if (p.Length == 2 && int.TryParse(p[0], out int a) && int.TryParse(p[1], out int b)) s_placed.Add(Key(a, b));
                    }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatHalo] reading the ring vegetation record threw: {ex.Message}"); }
            FiresLogger.LogInfo($"[FatHalo] ring vegetation: {s_placed.Count} ring cell(s) already placed in '{world}'");
        }

        private static void Record(string world, int cu, int cv)
        {
            s_placed.Add(Key(cu, cv));
            try
            {
                string path = PathFor(world);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.AppendAllText(path, cu + "," + cv + "\n");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FatHalo] recording ring cell {cu},{cv} threw: {ex.Message}"); }
        }

        private static bool PlaceCell(string world, int cu, int cv, int around, Vector3 o, Vector3 d, float r, float w, Vector3 ref0, Vector3 ref1)
        {
            if (s_mask == 0) s_mask = LayerMask.GetMask("terrain");
            float circ = around * CellSize;
            Vector3 OnRing(float uu, float vv, out Vector3 outward)
            {
                float a = uu / circ * 2f * Mathf.PI;
                outward = ref0 * Mathf.Cos(a) + ref1 * Mathf.Sin(a);
                return o + d * vv + outward * r;
            }
            Vector3 centre = OnRing((cu + 0.5f) * CellSize, (cv + 0.5f) * CellSize, out Vector3 outC);
            if (!Physics.Raycast(centre - outC * RayLift, outC, RayReach, s_mask, QueryTriggerInteraction.Ignore)) return false;   // floor not built yet

            int seed = ZNet.World != null ? ZNet.World.m_seed : 0;
            int placed = 0;
            foreach (var veg in ZoneSystem.instance.m_vegetation)
            {
                if (veg == null || !veg.m_enable || veg.m_prefab == null) continue;
                var rng = new System.Random(unchecked(seed + cu * 4271 + cv * 9187 + veg.m_prefab.name.GetStableHashCode()));
                int count;
                if (veg.m_max < 1f) count = rng.NextDouble() > veg.m_max ? 0 : 1;
                else count = rng.Next((int)veg.m_min, (int)veg.m_max + 1);
                if (count <= 0) continue;
                float cosMax = Mathf.Cos(Mathf.Deg2Rad * veg.m_maxTilt), cosMin = Mathf.Cos(Mathf.Deg2Rad * veg.m_minTilt);
                for (int i = 0; i < count; i++)
                {
                    float gu = (cu + (float)rng.NextDouble()) * CellSize, gv = (cv + (float)rng.NextDouble()) * CellSize;
                    int group = rng.Next(veg.m_groupSizeMin, veg.m_groupSizeMax + 1);
                    for (int j = 0; j < group; j++)
                    {
                        float pu = gu, pv = gv;
                        if (j > 0 && veg.m_groupRadius > 0f)
                        {
                            float a = (float)rng.NextDouble() * Mathf.PI * 2f, rr = Mathf.Sqrt((float)rng.NextDouble()) * veg.m_groupRadius;
                            pu += Mathf.Cos(a) * rr; pv += Mathf.Sin(a) * rr;
                        }
                        float spin = (float)rng.NextDouble() * 360f;
                        float scale = Mathf.Lerp(veg.m_scaleMin, veg.m_scaleMax, (float)rng.NextDouble());
                        if (Mathf.Abs(pv) > w - FatHaloArc.RimThickness - 1f) continue;   // on the band, off the rim lips
                        Vector3 p = OnRing(pu, pv, out Vector3 outward);
                        if (!FatHaloBiome.TryBiomeAt(p, out var biome) || (veg.m_biome & biome) == 0) continue;
                        if (!Physics.Raycast(p - outward * RayLift, outward, out RaycastHit hit, RayReach, s_mask, QueryTriggerInteraction.Ignore)) continue;
                        Vector3 up = -outward;
                        float tilt = Vector3.Dot(hit.normal, up);
                        if (tilt > cosMin || tilt < cosMax) continue;
                        var rot = Quaternion.AngleAxis(spin, up) * Quaternion.FromToRotation(Vector3.up, up);
                        var go = UnityEngine.Object.Instantiate(veg.m_prefab, hit.point + up * veg.m_groundOffset, rot);
                        var nview = go.GetComponent<ZNetView>();
                        if (nview != null && !Mathf.Approximately(scale, 1f)) nview.SetLocalScale(new Vector3(scale, scale, scale));
                        placed++;
                    }
                }
            }
            Record(world, cu, cv);
            s_cellsPlaced++; s_objectsPlaced += placed;
            if (s_cellsPlaced <= 3 || s_cellsPlaced % 25 == 0)
                FiresLogger.LogDebug($"[FatHalo] ring vegetation: cell ({cu},{cv}) placed {placed} object(s) ({s_cellsPlaced} cell(s), {s_objectsPlaced} object(s) this session)");
            return true;
        }
    }
}
