using System.Collections.Generic;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Performance
{
    // ── BIOME VEGETATION DENSITY ─────────────────────────────────────────
    //
    // Trees are the tri budget. Clutter density already had a lever
    // (ClutterDensityScale) but the TREE/BUSH layer had none, and a Meadows
    // or Black Forest zone can hand the renderer tens of millions of
    // triangles on its own.
    //
    // ZoneSystem.PlaceVegetation:653-660 decides how many of an entry to
    // place, and it reads exactly two numbers:
    //
    //     int num2 = 1;
    //     if (m_max < 1.0)  { if (Random.value > m_max) continue; }   // rare: probability
    //     else              num2 = Random.Range((int)m_min, (int)m_max + 1);
    //
    // So scaling m_min/m_max scales BOTH branches, and it degrades
    // gracefully: an entry scaled from m_max 2 down to 0.8 stops being
    // "1-2 per zone" and becomes "80% chance of one", which is what thinning
    // should do at the bottom end.
    //
    // Applied as a prefix/finalizer pair around PlaceVegetation rather than
    // by editing the list once at startup. The list is not ours — ExpandWorld
    // Data rewrites it on load and on reload — so a one-shot edit would be
    // clobbered, and a re-applied one would compound each time it ran.
    // Scale on the way in, restore on the way out: nothing persists, nothing
    // compounds, and it cannot be lost no matter who else writes the list.
    //
    // Determinism is preserved. PlaceVegetation seeds Random per entry from
    // (world seed, zone, prefab name) at :652 — never from m_min/m_max — so a
    // given zone still generates identically for the same density setting.
    // Zones ALREADY generated keep their vegetation; these are ZDOs now, not
    // decisions. A density change only affects zones generated after it.
    // Apply is a prefix and Restore a finalizer of FAT's one ZoneSystem.PlaceVegetation hook (FatVanillaHooks).
    internal static class ZoneSystem_PlaceVegetation_Density
    {
        private static readonly List<float> s_minBackup = new List<float>(256);
        private static readonly List<float> s_maxBackup = new List<float>(256);
        private static readonly Dictionary<GameObject, bool> s_exempt = new Dictionary<GameObject, bool>();
        private static bool s_scaled;
        private static bool s_logged;

        private static float Scale
        {
            get
            {
                float v = FiresVoxelConfig.VegetationDensityScale?.Value ?? 1f;
                return Mathf.Clamp(v, 0.02f, 2f);
            }
        }

        // Ore, pickables and spawners are gameplay, not scenery: they cost
        // almost nothing to draw and thinning them silently changes how the
        // world plays. Density is a rendering lever, so it only touches
        // things placed for looks. Component-tested rather than name-matched
        // so it holds for modded prefabs too.
        private static bool IsExempt(GameObject prefab)
        {
            if (prefab == null) return true;
            if (s_exempt.TryGetValue(prefab, out bool cached)) return cached;
            bool exempt = prefab.GetComponentInChildren<Pickable>(true) != null
                       || prefab.GetComponentInChildren<SpawnArea>(true) != null
                       || prefab.GetComponentInChildren<MineRock>(true) != null
                       || prefab.GetComponentInChildren<MineRock5>(true) != null;
            s_exempt[prefab] = exempt;
            return exempt;
        }

        // The first zone a server generated used to classify every vegetation prefab here ([perf] R32: 197 ms inside the
        // first explorer's zone). ZoneSystem.Start does it instead, while the world loads; a prefab added to the list later
        // (Expand World Data) is classified when first met. Only a server or host generates zones.
        internal static void WarmExemptions(ZoneSystem zoneSystem)
        {
            if (Mathf.Approximately(Scale, 1f) || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var veg = zoneSystem != null ? zoneSystem.m_vegetation : null;
            if (veg == null || veg.Count == 0) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var item in veg)
                if (item != null) IsExempt(item.m_prefab);
            FiresLogger.LogInfo($"[FiresVeg] classified {s_exempt.Count} vegetation prefab(s) for the density scale while the world "
                + $"loads, in {clock.ElapsedMilliseconds} ms, so the first new zone does not.");
        }

        internal static void Apply(ZoneSystem __instance)
        {
            s_scaled = false;
            float k = Scale;
            if (Mathf.Approximately(k, 1f)) return;
            var veg = __instance.m_vegetation;
            if (veg == null || veg.Count == 0) return;

            s_minBackup.Clear();
            s_maxBackup.Clear();
            int scaled = 0, exempt = 0;
            for (int i = 0; i < veg.Count; i++)
            {
                var v = veg[i];
                s_minBackup.Add(v != null ? v.m_min : 0f);
                s_maxBackup.Add(v != null ? v.m_max : 0f);
                if (v == null || IsExempt(v.m_prefab)) { exempt++; continue; }
                v.m_min *= k;
                v.m_max *= k;
                scaled++;
            }
            s_scaled = true;

            if (!s_logged)
            {
                s_logged = true;
                FiresLogger.LogInfo($"[FiresVeg] vegetation density ×{k:0.00} — scaling {scaled} entr(ies), " +
                          $"{exempt} exempt (pickables/ore/spawners keep vanilla counts).");
            }
        }

        internal static void Restore(ZoneSystem __instance)
        {
            if (!s_scaled) return;
            s_scaled = false;
            var veg = __instance.m_vegetation;
            if (veg == null) return;
            int n = Mathf.Min(veg.Count, s_minBackup.Count);
            for (int i = 0; i < n; i++)
            {
                if (veg[i] == null) continue;
                veg[i].m_min = s_minBackup[i];
                veg[i].m_max = s_maxBackup[i];
            }
        }
    }
}
