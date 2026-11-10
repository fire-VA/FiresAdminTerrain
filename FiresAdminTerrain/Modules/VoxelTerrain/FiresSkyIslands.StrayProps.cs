using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    internal static partial class FiresSkyIslands
    {
        // 0.2.1080 (Fire: "skylands are still drawing veg off the islands"): 1079 stops NEW disc-ring props on skylands, but the
        // ones already saved (Rock_4, Rock_3, stubbe, FirTree_oldLog, root08: the plain-island ring that also ran for skylands)
        // stay as world objects, hanging in the air or lying where they fell. Once per world, on the server, those prefabs near a
        // skyland are destroyed when they sit over empty air (no shaped island solid under them) or lie on the ground below the
        // skyland's footprint. Never anything with a creator, tamed, a player, a tombstone or a dropped item. The flag file
        // <islands file>.ringsweep keeps it from running again.
        private static readonly HashSet<string> s_ringPrefabs = new HashSet<string> { "Rock_4", "Rock_3", "stubbe", "FirTree_oldLog", "root08" };
        private static bool s_ringSweepDone;
        private static readonly HashSet<int> s_ringHashes = new HashSet<int>();

        // 0.2.1086 (Fire shot 517, -604,536: roots/stumps/logs floating and fallen round a skyland, after the 1080 sweep had run
        // at 22:11): sweep 2. The 1080 sweep ran once per world and only took the five ring prefabs; the strays since were the
        // super-flat island vegetation placed past the built rim (trees, logs, stumps, roots: fixed at the source in
        // FatSuperFlat.IslandItemsOnly). Sweep 2 runs once more per world (flag .ringsweep2) with every tree, log, stump and root
        // prefab, by the same two tests (over island air; or lying on the ground under the skyland). It also no longer marks
        // itself done before the skylands are loaded (1080 could, and then never ran that session).
        private static bool IsStrayKind(GameObject prefab)
        {
            if (prefab == null) return false;
            if (s_ringPrefabs.Contains(prefab.name)) return true;
            if (prefab.GetComponent<TreeBase>() != null || prefab.GetComponent<TreeLog>() != null) return true;
            string n = prefab.name.ToLowerInvariant();
            return prefab.GetComponent<Destructible>() != null && (n.Contains("stub") || n.Contains("root") || n.Contains("_log") || n.Contains("oldlog"));
        }

        internal static void SweepStrayRingProps()
        {
            if (s_ringSweepDone || ZDOMan.instance == null || ZNetScene.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var world = ZNet.World;
            if (world == null) return;
            var skylands = new List<Island>();
            foreach (var isl in s_islands) if (IsSkyland(isl.Kind)) skylands.Add(isl);
            if (skylands.Count == 0) return;   // not loaded yet: try again next server check
            s_ringSweepDone = true;
            string flag = RegistryPath(world.m_name) + ".ringsweep2";
            if (File.Exists(flag)) return;
            var hashes = new HashSet<int>();
            foreach (var n in s_ringPrefabs) { hashes.Add(n.GetStableHashCode()); s_ringHashes.Add(n.GetStableHashCode()); }
            foreach (var prefab in ZNetScene.instance.m_prefabs) if (IsStrayKind(prefab)) hashes.Add(prefab.name.GetStableHashCode());
            var all = new List<ZDO>();
            if (s_zdosById?.GetValue(ZDOMan.instance) is System.Collections.IDictionary dict)
                foreach (System.Collections.DictionaryEntry e in dict) if (e.Value is ZDO z && hashes.Contains(z.GetPrefab())) all.Add(z);
            int air = 0, fallen = 0;
            var near = new HashSet<int>();
            for (int k = 0; k < all.Count; k++)
            {
                var zdo = all[k];
                if (zdo.GetLong(ZDOVars.s_creator) != 0L) continue;
                Vector3 p = zdo.GetPosition();
                for (int i = 0; i < skylands.Count; i++)
                {
                    var isl = skylands[i];
                    float dx = p.x - WorldX(in isl), dz = p.z - WorldZ(in isl), reach = isl.R + 16f;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    bool overAir = p.y > isl.Bot + isl.OffY - 10f && p.y < isl.Top + isl.OffY + 25f && !TryIslandAt(p, 3f, out _, out _, out _);
                    bool onGround = !overAir && p.y < isl.Bot + isl.OffY - 10f;   // below the island: fell to the ground under it
                    // 0.2.1086: a tree/log/stump/root beyond the five ring prefabs counts as fallen only on super flat, whose ground
                    // grows nothing of its own; on a normal world the forest under a skyland is the ground's.
                    if (onGround && !FatSuperFlat.On && !s_ringHashes.Contains(zdo.GetPrefab())) onGround = false;
                    if (!overAir && !onGround) continue;
                    ZDOMan.instance.DestroyZDO(zdo);
                    if (overAir) air++; else fallen++;
                    near.Add(i);
                    break;
                }
            }
            try { File.WriteAllText(flag, "0.2.1086 stray skyland prop sweep 2 done"); } catch { }
            FiresLogger.LogInfo($"[FiresSkyIslands] sweep 2: removed {air + fallen} stray skyland prop(s) (air {air}, fallen {fallen}) near {near.Count} skyland(s); {hashes.Count} tree/log/stump/root prefab(s) checked over {all.Count} object(s)");
        }

        // 0.2.1080 (Fire: "the island is planned to be one size and then changes as it renders"): proof per dressed skyland
        // that placement and build share one shape. Per 8 angles: the edge placement uses (EdgeRadius: SkylandColumn +
        // SolidSpan) and the edge the mesher builds (SkylandColumn + BodyDensity > 0 at the column's middle height), plus the
        // registry's bounding radius R (a cull bound only, used by nothing that places).
        internal static string FootprintLine(int index)
        {
            if (index < 0 || index >= s_islands.Count) return null;
            var isl = s_islands[index];
            if (!IsSkyland(isl.Kind)) return null;
            var sb = new System.Text.StringBuilder();
            sb.Append("[FiresSkyIslands] island ").Append(index).Append(" footprint: registry bound R=").Append(isl.R.ToString("F1"))
              .Append("; placement edge / built edge per 8 angles: ");
            float worst = 0f;
            for (int a = 0; a < 8; a++)
            {
                float ang = a * Mathf.PI / 4f;
                float placed = EdgeRadius(in isl, ang), built = BuiltEdge(in isl, ang);
                worst = Mathf.Max(worst, Mathf.Abs(placed - built));
                sb.Append(placed.ToString("F1")).Append('/').Append(built.ToString("F1")).Append(' ');
            }
            return sb.Append("; max mismatch ").Append(worst.ToString("F1")).Append(" m").ToString();
        }

        private static float BuiltEdge(in Island isl, float a)
        {
            float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
            for (float r = isl.R * 0.999f; r > 1f; r -= 0.5f)
            {
                float x = isl.X + cx * r, z = isl.Z + cz * r;
                if (!SkylandColumn(in isl, x, z, out float t, out float b, out float side)) continue;
                float mid = (t + b) * 0.5f;
                if (BodyDensity(mid, t, b, side) > 0f || BodyDensity(t - 0.5f, t, b, side) > 0f) return r;
            }
            return 0f;
        }
    }
}
