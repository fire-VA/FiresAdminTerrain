using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1096 (Fire: "i think our flat worlds no locations logic screwed us on skyland and cave location placement"; research
    // Tools\_worldgen_locations_1096.md). One line that says where a super-flat world's locations went:
    //   overworld skipped N  - location types super flat never plans on the flat ground (FatSuperFlat.NoLocationPlans);
    //   cave planned P / placed Q / refused R - instances of cave-eligible types in the world's location list, how many have
    //       spawned, the sites the plan-time cave test refused while planning (reasons) and the ones the spawn gate refused;
    //   skyland sites S of I - islands holding a location of their biome, and why the rest hold none.
    // Logged once after a location generation finishes and once per session ~90 s after the world loads (Info), then again only
    // when a count changes (at most once a minute). Server and single-player; nothing on a pure client.
    internal static class FatLocationsReport
    {
        private static readonly HashSet<string> s_overworldSkipped = new HashSet<string>();
        private static int s_planRefused, s_tooDeep, s_noAccess, s_biome, s_cover, s_other, s_cavePasses;
        private static float s_sayAt = -1f, s_nextAllowed;
        private static string s_last;

        internal static void NoteOverworldSkipped(string prefab)
        {
            if (prefab != null && s_overworldSkipped.Add(prefab)) Dirty(5f);
        }

        internal static void NoteCavePass(int refused, int tooDeep, int noAccess, int biome, int cover, int other)
        {
            s_cavePasses++;
            s_planRefused += refused; s_tooDeep += tooDeep; s_noAccess += noAccess; s_biome += biome; s_cover += cover; s_other += other;
            Dirty(5f);
        }

        private static void Dirty(float delay)
        {
            float t = Time.realtimeSinceStartup + delay;
            if (s_sayAt < 0f || t < s_sayAt) s_sayAt = t;
        }

        internal static string Line()
        {
            var zs = ZoneSystem.instance;
            int planned = 0, placed = 0;
            if (zs != null && zs.m_locationInstances != null)
                foreach (var inst in zs.m_locationInstances.Values)
                {
                    if (inst.m_location == null || !FatUndergroundLocations.MayGoUnderground(inst.m_location)) continue;
                    planned++;
                    if (inst.m_placed) placed++;
                }
            string plan = s_cavePasses > 0
                ? $"refused at planning {s_planRefused} (too deep {s_tooDeep}, no access {s_noAccess}, biome {s_biome}, cover {s_cover}, other {s_other})"
                : "planning refusals n/a (planned in an earlier session)";
            int islands = FiresSkyIslands.SkylandCount();
            string none = $"none: chance {FiresSkyIslands.SitesNoChance}, room {FiresSkyIslands.SitesNoRoom}, no dry spot {FiresSkyIslands.SitesNoDry}, pond with no water map {FiresSkyIslands.SitesNoPondDry}; pond islands {FiresSkyIslands.SitesPondIslands}; "
                + $"0.2.1137 rule on {FiresSkyIslands.SitesRule3Islands} island(s): pond dry sites {FiresSkyIslands.SitesPondDry}, mini sites {FiresSkyIslands.SitesMini}";
            return $"[Locations] super flat: overworld skipped {s_overworldSkipped.Count} type(s), cave planned {planned} / placed {placed} / {plan}, "
                + $"refused at spawn {FatSuperFlat.SurfaceRefusedThisSession} this session; skyland sites {FiresSkyIslands.SitesYes} of {islands} ({none})";
        }

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class Tick
        {
            private static void Postfix()
            {
                if (!FatSuperFlat.On || ZNet.instance == null || !ZNet.instance.IsServer()) return;
                float now = Time.realtimeSinceStartup;
                if (s_sayAt < 0f) s_sayAt = now + 90f;   // once per session after the world loads
                if (now < s_sayAt || now < s_nextAllowed) return;
                s_sayAt = float.MaxValue;
                s_nextAllowed = now + 60f;
                string line = Line();
                if (line == s_last) return;
                s_last = line;
                FiresLogger.LogInfo(line);
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class Reset
        {
            private static void Postfix()
            {
                s_overworldSkipped.Clear();
                s_planRefused = s_tooDeep = s_noAccess = s_biome = s_cover = s_other = s_cavePasses = 0;
                s_sayAt = -1f; s_nextAllowed = 0f; s_last = null;
            }
        }
    }
}
