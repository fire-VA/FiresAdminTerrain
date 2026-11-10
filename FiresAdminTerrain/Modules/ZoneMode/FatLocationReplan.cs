using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // 0.2.1137 (Fire: "Yes, prove cause then build" / "Yes, fit more islands"; proof: Tools\PLAN_cave_island_locations_20261027.md).
    // A super flat world made before 0.2.1137 planned no cave locations (every dug cavern landed under 'Cave Min Y') and gave
    // 18 of 26 sky islands no site. A world made on 0.2.1137+ gets the new rules at creation (FatUndergroundLocations' super-flat
    // depth stack, FiresWorldProfile.SkySites3). For an EXISTING world, two add-only steps, server only, once per world, gated
    // by 'Apply location replan to existing worlds' (server-owned, default off, so SFSC3 only takes them after the fresh-world proof):
    //   * caves: each cave-eligible location type below its quota gets FatLocationAccelerator's pass again; it plans only in
    //     zones NOT generated yet (Pass.Generated) and registers unplaced instances, which vanilla spawns when those zones first
    //     load. Placed instances and explored zones are never touched (Fire: no locations in already-explored cave zones).
    //   * islands: every skyland with no site and no generated zone under its footprint is recorded in the world's profile
    //     (SiteIslands3); from the NEXT load on, server and clients (the profile rides the handshake) roll it with the 1137 rule
    //     (a dry site over a pond, a mini site when nothing else fits). Islands with built ground keep their shape.
    // Markers + decisions: FiresWorldGen\<world>\firesgen_<world>_locreplan.txt.
    internal static class FatLocationReplan
    {
        internal static bool SiteLogOn => true;   // the [SkySite] lines are Debug

        private static string s_world;
        private static bool s_doneThisSession, s_gateSaid, s_sawGenerating, s_firstSeen;
        private static readonly AccessTools.FieldRef<ZoneSystem, HashSet<Vector2s>> GeneratedZones =
            AccessTools.FieldRefAccess<ZoneSystem, HashSet<Vector2s>>("m_generatedZones");

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class Tick
        {
            private static void Postfix(ZoneSystem __instance)
            {
                try { Step(__instance); }
                catch (Exception ex) { s_doneThisSession = true; FiresLogger.LogWarning($"[LocReplan] stopped: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class Reset
        {
            private static void Postfix() { s_world = null; s_doneThisSession = s_gateSaid = s_sawGenerating = s_firstSeen = false; }
        }

        private static void Step(ZoneSystem zs)
        {
            if (s_doneThisSession || zs == null || ZNet.instance == null || !ZNet.instance.IsServer() || ZNet.World == null) return;
            var profile = FiresWorldProfile.Current;
            if (profile == null) { s_doneThisSession = true; return; }
            string world = ZNet.World.m_name;
            if (s_world != world) { s_world = world; s_firstSeen = false; s_sawGenerating = false; }
            // A world whose locations are generated THIS session is new: it planned with the 1137 rules already.
            if (!s_firstSeen) { s_firstSeen = true; s_sawGenerating = !zs.LocationsGenerated; }
            if (!zs.LocationsGenerated) return;
            if (ZNet.instance.GetTimeSeconds() <= 0 || (GeneratedZones(zs)?.Count ?? 0) == 0) return;   // the save not loaded yet

            string marker = FiresWorldRecords.PathFor(world, "_locreplan.txt");
            var done = ReadMarker(marker);
            bool newWorld = s_sawGenerating || profile.SkySites3;
            if (newWorld)
            {
                if (!done.ContainsKey("caves")) AppendMarker(marker, "caves", "new world: planned at creation with the 0.2.1137 rules");
                if (!done.ContainsKey("islands")) AppendMarker(marker, "islands", "new world: every island uses the 0.2.1137 site rule");
                s_doneThisSession = true;
                return;
            }
            bool gate = FatLocationAccelerator.ApplyReplanExisting != null && FatLocationAccelerator.ApplyReplanExisting.Value;
            if (!gate)
            {
                if (!s_gateSaid && (!done.ContainsKey("caves") || !done.ContainsKey("islands")))
                    FiresLogger.LogInfo($"[LocReplan] world '{world}' predates 0.2.1137: the cave-location replan and the sky island site decision are "
                        + "SKIPPED ('Apply location replan to existing worlds' is off).");
                s_gateSaid = true;
                s_doneThisSession = true;
                return;
            }
            s_doneThisSession = true;
            if (!done.ContainsKey("islands")) DecideIslands(world, profile, zs, marker);
            if (!done.ContainsKey("caves")) zs.StartCoroutine(ReplanCaves(zs, world, marker));
        }

        // ── islands ─────────────────────────────────────────────────────────────────────────────────────────────────
        private static void DecideIslands(string world, FiresWorldProfile profile, ZoneSystem zs, string marker)
        {
            var generated = GeneratedZones(zs) ?? new HashSet<Vector2s>();
            int on = 0, off = 0;
            var lines = new List<string>();
            foreach (var (variety, x, z, r) in FiresSkyIslands.SkylandsWithoutSite())
            {
                bool built = false;
                var zmin = ZoneSystem.GetZone(new Vector3(x - r, 0f, z - r));
                var zmax = ZoneSystem.GetZone(new Vector3(x + r, 0f, z + r));
                for (int zz = zmin.y; zz <= zmax.y && !built; zz++)
                    for (int zx = zmin.x; zx <= zmax.x && !built; zx++)
                        if (generated.Contains(new Vector2s(zx, zz))) built = true;
                if (built)
                {
                    off++;
                    lines.Add($"island {variety} off (built ground under it)");
                    FiresLogger.LogDebug($"[SkySite] island {variety} ({x:0},{z:0}): skipped: its ground is already built (a generated zone under it); keeps its shape");
                    continue;
                }
                on++;
                profile.SiteIslands3.Add(variety);
                lines.Add($"island {variety} on");
                FiresLogger.LogDebug($"[SkySite] island {variety} ({x:0},{z:0}): switched to the 0.2.1137 site rule (no generated zone under it); takes effect on the next world load");
            }
            if (on > 0) profile.Write(world);
            AppendMarker(marker, "islands", $"decided: {on} island(s) on the 0.2.1137 site rule, {off} kept (built); " + string.Join("; ", lines));
            FiresLogger.LogInfo($"[SkySite] world '{world}': {on} site-less island(s) switched to the 0.2.1137 site rule (from the next load), {off} kept (built ground).");
        }

        // ── caves ───────────────────────────────────────────────────────────────────────────────────────────────────
        private static IEnumerator ReplanCaves(ZoneSystem zs, string world, string marker)
        {
            if (!FatSuperFlat.CavesOn)
            {
                AppendMarker(marker, "caves", "skipped: not a super flat world with caves on");
                yield break;
            }
            var clock = Stopwatch.StartNew();
            var added = new Dictionary<string, int>();
            int types = 0;
            foreach (var l in zs.m_locations.ToList())
            {
                if (l == null || !l.m_enable || l.m_prefab == null || !l.m_prefab.IsValid || l.m_prioritized || l.m_unique) continue;
                if (!FatUndergroundLocations.MayGoUnderground(l)) continue;
                int before = zs.m_locationInstances.Values.Count(i => i.m_location == l);
                if (before >= l.m_quantity) continue;
                types++;
                var pkg = new ZPackage();
                yield return FatLocationAccelerator.Place(zs, l, pkg);
                int after = zs.m_locationInstances.Values.Count(i => i.m_location == l);
                if (after > before) added[l.m_prefab.Name] = after - before;
            }
            int total = added.Values.Sum();
            string by = string.Join(", ", added.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"));
            FiresLogger.LogInfo($"[CaveLoc] replan: {total} cave site(s) added in ungenerated zones (by type {(by.Length > 0 ? by : "none")}); "
                + $"{types} type(s) below quota re-planned in {clock.Elapsed.TotalSeconds:0.0} s; placed instances and explored zones untouched.");
            AppendMarker(marker, "caves", $"replanned: {total} site(s) added ({by})");
        }

        // ── marker file: one "key: text" line per step ─────────────────────────────────────────────────────────────
        private static Dictionary<string, string> ReadMarker(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(path)) return d;
                foreach (var line in File.ReadAllLines(path))
                {
                    int c = line.IndexOf(':');
                    if (c > 0) d[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[LocReplan] marker {path}: {ex.Message}"); }
            return d;
        }

        private static void AppendMarker(string path, string key, string text)
        {
            try { File.AppendAllText(path, $"{key}: {text} ({DateTime.UtcNow:O}, FAT {VerdantsAscent.FiresAdminTerrain.PluginVersion}){Environment.NewLine}"); }
            catch (Exception ex) { FiresLogger.LogWarning($"[LocReplan] could not write {path}: {ex.Message}"); }
        }
    }
}
