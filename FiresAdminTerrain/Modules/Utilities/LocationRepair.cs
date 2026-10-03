using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Utilities
{
    // ── `va_location_repair dry|apply <world>`: re-point damaged location proxies (0.2.700, [fgn] + [lead] 10-01) ─────────
    // A location (dungeon, trader, altar...) lives in the world as one LocationProxy ZDO, which carries two ints: "location"
    // (the location prefab's name hash) and "seed" (world seed + zone.x * 4271 + zone.y * 9187, ZoneSystem.PlaceLocations).
    // Every load, the proxy re-spawns the location from those two, so a proxy whose ints were overwritten spawns the wrong
    // location or nothing. The world also saves its own list of placed locations (ZoneSystem.m_locationInstances: prefab hash,
    // position, placed), which is what this checks the proxies against:
    //   dry          read-only: for every PLACED instance, the proxy ZDO(s) in its zone within MatchRadius (flat) of it, and
    //                a report line per proxy: ok / damaged (prefab, position, old → new hash and seed) / duplicate / missing,
    //                plus proxies no instance claims (orphans). Totals to the console, every line to the report file.
    //   apply <world> rewrites the two ints on the damaged proxies only (one candidate, wrong ints) — the same plan the dry
    //                run of this world printed, refused when the plan changed. Missing, duplicate and orphan proxies are
    //                only reported. The location re-spawns from the new ints when its zone next loads (restart the world).
    // Server or single-player host only, and apply only with no other peer connected. Run it on a COPY of the world, never
    // a live one: sign text, trackers and other ZDO data that was overwritten is NOT restored; only a backup has that.
    internal static class LocationRepair
    {
        private const string Command = "va_location_repair", Tag = "[LocationRepair]";
        private const float MatchRadius = 2f;
        private const int ConsoleLines = 12;
        private static readonly int ProxyPrefab = "LocationProxy".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> ObjectsById =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static bool s_registered;
        private static string s_dryWorld, s_dryPlan;
        private static float s_dryAt;

        private struct Fix { public ZDO Zdo; public int Hash, Seed; }

        internal static void RegisterCommand()
        {
            if (s_registered) return;
            s_registered = true;
            new Terminal.ConsoleCommand(Command,
                "dry | apply <world name> - check every placed location's proxy (prefab hash + seed) against the world's own "
                + "location list; dry reports, apply rewrites only the damaged proxies the dry run listed. Server/host, on a COPY (admin)",
                args =>
                {
                    var ctx = args.Context;
                    Action<string> say = line => { FiresLogger.LogInfo(line); try { ctx?.AddString(line); } catch { } };
                    string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "";
                    if (mode == "dry") Run(false, null, say);
                    else if (mode == "apply") Run(true, args.Length > 2 ? string.Join(" ", args.Args.Skip(2)) : null, say);
                    else say($"{Tag} usage: {Command} dry   then   {Command} apply <world name>   (on a COPY of the world)");
                },
                isCheat: true);
        }

        private static void Run(bool apply, string confirm, Action<string> say)
        {
            if (ZNet.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null || WorldGenerator.instance == null)
            { say($"{Tag} no world loaded."); return; }
            if (!ZNet.instance.IsServer())
            { say($"{Tag} run it on the server: load the world copy in single-player (or host it) and run it there; nothing done."); return; }
            string world = ZNet.World != null ? ZNet.World.m_name : "?";
            if (apply)
            {
                if (confirm == null || !string.Equals(confirm.Trim(), world, StringComparison.Ordinal))
                { say($"{Tag} apply needs the world name to confirm: {Command} apply {world}   (on a COPY; nothing written)"); return; }
                int peers = ZNet.instance.GetConnectedPeers().Count;
                if (peers > 0) { say($"{Tag} {peers} other peer(s) connected: apply only runs with nobody else in the world; nothing written."); return; }
                if (s_dryWorld != world || s_dryPlan == null) { say($"{Tag} run '{Command} dry' on '{world}' first; nothing written."); return; }
            }

            var inv = CultureInfo.InvariantCulture;
            int worldSeed = WorldGenerator.instance.GetSeed();
            var names = new Dictionary<int, string>();
            foreach (var loc in ZoneSystem.instance.m_locations)
                if (loc?.m_prefab != null && !string.IsNullOrEmpty(loc.m_prefab.Name)) names[loc.m_prefab.Name.GetStableHashCode()] = loc.m_prefab.Name;
            string Name(int h) => h == 0 ? "none (0)" : names.TryGetValue(h, out var n) ? n : $"unknown ({h})";

            var byZone = new Dictionary<Vector2s, List<ZDO>>();
            int proxies = 0;
            foreach (var zdo in ObjectsById(ZDOMan.instance).Values)
            {
                if (zdo == null || zdo.GetPrefab() != ProxyPrefab) continue;
                proxies++;
                var zone = ZoneSystem.GetZone(zdo.GetPosition());
                if (!byZone.TryGetValue(zone, out var list)) byZone[zone] = list = new List<ZDO>();
                list.Add(zdo);
            }

            var lines = new List<string>();
            var fixes = new List<Fix>();
            var claimed = new HashSet<ZDO>();
            int placed = 0, ok = 0, damaged = 0, duplicate = 0, missing = 0;
            foreach (var kv in ZoneSystem.instance.m_locationInstances.OrderBy(k => k.Key.x).ThenBy(k => k.Key.y))
            {
                var inst = kv.Value;
                if (!inst.m_placed || inst.m_location?.m_prefab == null) continue;
                placed++;
                string prefab = inst.m_location.m_prefab.Name;
                int hash = prefab.GetStableHashCode(), seed = worldSeed + kv.Key.x * 4271 + kv.Key.y * 9187;
                var near = byZone.TryGetValue(kv.Key, out var zl)
                    ? zl.Where(z => Flat(z.GetPosition(), inst.m_position) <= MatchRadius).ToList() : new List<ZDO>();
                string at = string.Format(inv, "({0:0.0}, {1:0.0}, {2:0.0}) zone {3},{4}", inst.m_position.x, inst.m_position.y, inst.m_position.z, kv.Key.x, kv.Key.y);
                if (near.Count == 0) { missing++; lines.Add($"missing   {prefab} at {at}: no proxy within {MatchRadius:0} m (only reported)"); continue; }
                foreach (var z in near) claimed.Add(z);
                if (near.Count > 1)
                {
                    duplicate++;
                    foreach (var z in near)
                        lines.Add($"duplicate {prefab} at {at}: proxy {z.m_uid} holds {Name(z.GetInt(ZDOVars.s_location))} seed {z.GetInt(ZDOVars.s_seed)} (only reported)");
                    continue;
                }
                var p = near[0];
                int oldHash = p.GetInt(ZDOVars.s_location), oldSeed = p.GetInt(ZDOVars.s_seed);
                if (oldHash == hash && oldSeed == seed) { ok++; lines.Add($"ok        {prefab} at {at}"); continue; }
                damaged++;
                fixes.Add(new Fix { Zdo = p, Hash = hash, Seed = seed });
                lines.Add($"damaged   {prefab} at {at}: proxy {p.m_uid} hash {oldHash} ({Name(oldHash)}) → {hash}, seed {oldSeed} → {seed}");
            }
            int orphans = 0;
            foreach (var zl in byZone.Values)
                foreach (var z in zl)
                {
                    if (claimed.Contains(z)) continue;
                    orphans++;
                    var pos = z.GetPosition();
                    lines.Add(string.Format(inv, "orphan    proxy {0} at ({1:0.0}, {2:0.0}, {3:0.0}) holds {4} seed {5}: no placed location instance here (only reported)",
                        z.m_uid, pos.x, pos.y, pos.z, Name(z.GetInt(ZDOVars.s_location)), z.GetInt(ZDOVars.s_seed)));
                }

            string plan = string.Join(";", fixes.Select(f => $"{f.Zdo.m_uid}:{f.Hash}:{f.Seed}"));
            string totals = $"world '{world}' seed {worldSeed}: {placed} placed location(s), {proxies} proxy ZDO(s) — {ok} ok, {damaged} damaged, "
                + $"{duplicate} duplicate, {missing} missing, {orphans} orphan proxy(ies)";
            if (!apply)
            {
                s_dryWorld = world; s_dryPlan = plan; s_dryAt = Time.realtimeSinceStartup;
                string file = WriteReport(world, "dry", totals, lines);
                say($"{Tag} DRY {totals}. Nothing written.{(damaged > 0 ? $" To fix the {damaged} damaged: {Command} apply {world}   (on a COPY)" : "")}");
                foreach (var l in lines.Where(l => !l.StartsWith("ok")).Take(ConsoleLines)) say($"{Tag}   {l}");
                say($"{Tag} every line: {file ?? "(report file not written)"}");
                return;
            }
            if (plan != s_dryPlan)
            { say($"{Tag} the plan changed since the dry run {Time.realtimeSinceStartup - s_dryAt:0} s ago ({damaged} damaged now): run dry again; nothing written."); return; }
            if (fixes.Count == 0) { say($"{Tag} APPLY nothing to fix: {totals}."); return; }
            foreach (var f in fixes)
            {
                f.Zdo.Set(ZDOVars.s_location, f.Hash, true);
                f.Zdo.Set(ZDOVars.s_seed, f.Seed, true);
            }
            s_dryPlan = null;
            string applied = WriteReport(world, "apply", totals, lines.Where(l => l.StartsWith("damaged")).Select(l => "fixed " + l).ToList());
            say($"{Tag} APPLY rewrote {fixes.Count} damaged proxy(ies) on '{world}'. They re-spawn their location when the zone next loads: "
                + $"save and restart the world. Missing/duplicate/orphan left alone. Report: {applied ?? "(not written)"}");
        }

        private static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        // BepInEx\FiresReports\location_repair_<world>_<mode>_<time>.txt (inside the game folder).
        private static string WriteReport(string world, string mode, string totals, List<string> lines)
        {
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresReports");
                System.IO.Directory.CreateDirectory(folder);
                string safe = new string(world.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
                string path = System.IO.Path.Combine(folder, $"location_repair_{safe}_{mode}_{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                System.IO.File.WriteAllLines(path, new[] { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Tag} {mode.ToUpperInvariant()} {totals}" }.Concat(lines));
                return path;
            }
            catch (Exception e) { FiresLogger.LogWarning($"{Tag} report not written: {e.Message}"); return null; }
        }
    }
}
