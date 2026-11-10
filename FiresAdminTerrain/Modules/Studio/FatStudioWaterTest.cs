using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using VerdantsAscent.Water.Tools;

namespace VerdantsAscent.Terrain.Studio
{
    // 0.2.1246: `fat_studio_water_test` (host, admin), 30 m in front of you on dry land: (1) pond place -> water reads at its centre
    // -> undo -> region identical; (2) a short river place -> water on its path -> undo -> identical; (3) vegetation remove in a
    // 6 m circle -> fewer plants -> undo -> identical. "identical" = the touched zones' terrain data, the water at the probe points
    // and every kept object (prefab + position) in the area read the same as before the edit. Waits for the async water each time.
    internal static class FatStudioWaterTest
    {
        private const string Tag = "[FatStudio]";
        private const float Settle = 5f;

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("fat_studio_water_test", "- host: Studio water (pond, river) + vegetation place / undo round trips 30 m in front of you; PASS = identical after undo",
                args =>
                {
                    var me = Player.m_localPlayer;
                    if (me == null || FiresAdminTerrain.Instance == null) { args.Context?.AddString($"{Tag} no player"); return; }
                    FiresAdminTerrain.Instance.StartCoroutine(Run(me.transform.position + me.transform.forward * 30f, me.transform.forward, args.Context));
                }, isCheat: true);
        }

        private static IEnumerator Run(Vector3 at, Vector3 fwd, Terminal ctx)
        {
            int pass = 0, fail = 0;
            void Say(string line) { FiresLogger.LogInfo($"{Tag} {line}"); ctx?.AddString($"{Tag} {line}"); }
            fwd.y = 0f; fwd.Normalize();
            var side = new Vector3(fwd.z, 0f, -fwd.x);

            // 1) pond
            {
                var probes = new[] { at };
                string before = Print(at, 24f, probes); var cap = new List<FatStudioRegion.Circle> { Cap(at, 6f * WaterShapingTools.BankSpread + 6f) }; string tBefore = TerrainPrint(cap);
                if (!FatStudioWater.PlacePond(new FatPondArgs { Center = at, Radius = 6f, Depth = 2f }, out var h, out var s, out string why)) { fail++; Say($"FAIL pond place: {why}"); }
                else
                {
                    yield return new WaitForSecondsRealtime(Settle);
                    bool wet = Wet(at, out float d);
                    if (wet) { pass++; Say($"PASS pond place: {why}; water {d:0.0} m deep at the centre"); } else { fail++; Say($"FAIL pond place: no water at the centre after {Settle:0} s ({why})"); }
                    yield return Undo(s, at, 24f, probes, before, cap, tBefore, "pond", ok => { if (ok) pass++; else fail++; }, Say);
                }
            }
            // 2) river: three points across the front, 12 m apart
            {
                var path = new[] { at + side * 12f + fwd * 20f, at + fwd * 20f, at - side * 12f + fwd * 20f };
                var probes = new[] { path[1] };
                string before = Print(path[1], 30f, probes); var cap = new List<FatStudioRegion.Circle>(); foreach (var q in path) cap.Add(Cap(q, 2f * WaterShapingTools.BankSpread + 6f)); string tBefore = TerrainPrint(cap);
                for (float until = Time.realtimeSinceStartup + 20f; WaterToolDriver.StudioRiverCarving && Time.realtimeSinceStartup < until;) yield return null;
                if (!FatStudioWater.PlaceRiver(new FatRiverArgs { Path = path, Width = 4f, Depth = 1.5f }, out var h, out var s, out string why)) { fail++; Say($"FAIL river place: {why}"); }
                else
                {
                    for (float until = Time.realtimeSinceStartup + 30f; WaterToolDriver.StudioRiverCarving && Time.realtimeSinceStartup < until;) yield return null;
                    yield return new WaitForSecondsRealtime(Settle);
                    bool wet = Wet(path[1], out float d);
                    if (wet) { pass++; Say($"PASS river place: {why}; water {d:0.0} m deep mid-path"); } else { fail++; Say($"FAIL river place: no water mid-path after the carve ({why})"); }
                    yield return Undo(s, path[1], 30f, probes, before, cap, tBefore, "river", ok => { if (ok) pass++; else fail++; }, Say);
                }
            }
            // 3) vegetation remove
            {
                var c = at - fwd * 12f;
                var probes = new Vector3[0];
                string before = Print(c, 12f, probes); var cap = new List<FatStudioRegion.Circle> { Cap(c, 6f) }; string tBefore = TerrainPrint(cap);
                int had = Plants(c, 6f);
                if (!FatStudioWater.Vegetation(new FatVegArgs { Points = new[] { c }, Radius = 6f, Mode = FatVegMode.Remove }, out var s, out string why)) { fail++; Say($"FAIL vegetation remove: {why}"); }
                else
                {
                    yield return new WaitForSecondsRealtime(1f);
                    int now = Plants(c, 6f);
                    if (had == 0) Say($"note vegetation: no plants in the 6 m circle here ({why}); the undo is still checked");
                    if (now == 0) { pass++; Say($"PASS vegetation remove: {why}; {had} -> {now}"); } else { fail++; Say($"FAIL vegetation remove: {had} -> {now} ({why})"); }
                    yield return Undo(s, c, 12f, probes, before, cap, tBefore, "vegetation", ok => { if (ok) pass++; else fail++; }, Say);
                }
            }
            Say($"fat_studio_water_test done: {pass} PASS, {fail} FAIL at ({at.x:0},{at.z:0})");
        }

        private static IEnumerator Undo(FatSnapshot s, Vector3 c, float r, Vector3[] probes, string before, List<FatStudioRegion.Circle> cap, string tBefore, string what, Action<bool> tally, Action<string> say)
        {
            bool done = false; string why = null;
            for (float until = Time.realtimeSinceStartup + 20f; !done && Time.realtimeSinceStartup < until;)
            {
                done = FatStudioWater.Restore(s, out why);
                if (!done && why != null && why.StartsWith("busy", StringComparison.Ordinal)) { yield return new WaitForSecondsRealtime(0.5f); continue; }
                break;
            }
            if (!done) { tally(false); say($"FAIL {what} undo: {why}"); yield break; }
            // 0.2.1250 (round J: the only differences left were the terrain hash, also in a vegetation undo that changed nothing,
            // because the print circle was up to twice the snapshot's and was read 5 s later while the snow refill ran): the
            // footprint the snapshot captured, read in the same frame as the restore, must equal what was captured before the edit.
            string tAfter = TerrainPrint(cap);
            if (tAfter != tBefore) { tally(false); say($"FAIL {what} undo: the captured footprint's terrain differs right after the restore ({why}); {Diff(tBefore, tAfter)}"); yield break; }
            yield return new WaitForSecondsRealtime(Settle);
            string after = Print(c, r, probes);
            if (after == before) { tally(true); say($"PASS {what} undo: identical ({why})"); }
            else { tally(false); say($"FAIL {what} undo: not identical ({why}); {Diff(before, after)}"); }
        }

        // the zones' terrain hashes + water at the probes + every kept object in the circle, sorted
        private static string Print(Vector3 c, float r, Vector3[] probes)
        {
            var parts = new List<string>();
            FatStudioRegion.Zones(new List<FatStudioRegion.Circle> { new FatStudioRegion.Circle { X = c.x, Z = c.z, R = r } }, out var zones, out _);
            foreach (var p in probes) parts.Add(Wet(p, out _) ? "w+" : "w-");
            var one = new List<ZDO>();
            foreach (var z in zones)
            {
                one.Clear(); ZDOMan.instance.FindSectorObjects(z, new SimulationDistance(0, 0), one);
                foreach (var o in one)
                {
                    if (o == null || !o.IsValid() || o.GetLong(ZDOVars.s_creator) != 0L) continue;
                    var p = o.GetPosition(); if ((p.x - c.x) * (p.x - c.x) + (p.z - c.z) * (p.z - c.z) > r * r) continue;
                    var go = ZNetScene.instance.GetPrefab(o.GetPrefab());
                    if (go == null || go.GetComponent<Character>() != null || go.GetComponent<ItemDrop>() != null) continue;
                    var rb = go.GetComponent<Rigidbody>(); if (rb != null && !rb.isKinematic) continue;   // 0.2.1250: a barrel rolls 4 cm on its own
                    parts.Add($"{o.GetPrefab()}@{p.x:0.00},{p.z:0.00}");   // 0.2.1249: XZ only (a tree re-grounded by snow is the same tree)
                }
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts);
        }

        // 0.2.1247 (round H: the pond's water was laid but FatApi.TryWaterAt only answers generated water): placed or generated
        private static bool Wet(Vector3 p, out float depth)
        {
            depth = 0f;
            // 0.2.1249: a placed disk there (FatWaterChecks applies the fish / lily open-water rules and refused a freezing pond)
            foreach (var h in FatStudioWater.List(p, 1f)) if (h.Kind != "generated") { depth = 1f; return true; }
            return false;
        }

        // the parts only before and only after (prefab hash @ position, t<zone> terrain hashes, w water), up to 12 of each
        private static string Diff(string before, string after)
        {
            var b = new HashSet<string>(before.Split(' ')); var a = new HashSet<string>(after.Split(' '));
            var onlyB = new List<string>(); var onlyA = new List<string>();
            foreach (var x in b) if (!a.Contains(x)) onlyB.Add(x);
            foreach (var x in a) if (!b.Contains(x)) onlyA.Add(x);
            onlyB.Sort(StringComparer.Ordinal); onlyA.Sort(StringComparer.Ordinal);
            string Names(List<string> l) { var o = new List<string>(); foreach (var x in l) { if (o.Count >= 12) { o.Add("..."); break; } int at = x.IndexOf('@'); var go = at > 0 && int.TryParse(x.Substring(0, at), out int h) ? ZNetScene.instance.GetPrefab(h) : null; o.Add(go != null ? go.name + x.Substring(at) : x); } return string.Join(" ", o); }
            return $"only before ({onlyB.Count}): [{Names(onlyB)}]; only after ({onlyA.Count}): [{Names(onlyA)}]";
        }

        private static FatStudioRegion.Circle Cap(Vector3 p, float r) => new FatStudioRegion.Circle { X = p.x, Z = p.z, R = r };

        // the footprint's raw terrain vertices over the given circles, per zone (FatStudioTerrain.CaptureFoot, the snapshot's own read)
        private static string TerrainPrint(List<FatStudioRegion.Circle> circles)
        {
            FatStudioRegion.Zones(circles, out var zones, out _);
            var parts = new List<string>();
            foreach (var z in zones) { FatStudioTerrain.CaptureFoot(z, circles, out byte[] tc); parts.Add($"t{z.x},{z.y}:" + (tc == null ? "none" : Hex(tc))); }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts);
        }

        private static int Plants(Vector3 c, float r)
        {
            var veg = new HashSet<int>();
            foreach (var v in ZoneSystem.instance.m_vegetation) if (v?.m_prefab != null) veg.Add(v.m_prefab.name.GetStableHashCode());
            int n = 0; var one = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(c), new SimulationDistance(1, 0), one);
            foreach (var o in one)
            {
                if (o == null || !o.IsValid() || o.GetLong(ZDOVars.s_creator) != 0L || !veg.Contains(o.GetPrefab())) continue;
                var p = o.GetPosition(); if ((p.x - c.x) * (p.x - c.x) + (p.z - c.z) * (p.z - c.z) <= r * r) n++;
            }
            return n;
        }

        private static string Hex(byte[] b) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b), 0, 6); }
        private static string Short(string s) => s.Length > 400 ? s.Substring(0, 400) + "..." : s;
    }
}
