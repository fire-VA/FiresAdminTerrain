using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.Zones
{
    // FatApi.RegenerateZones (used by RPGM's admin "regenerate territory" command): whole zones
    // grow again as the world would grow them NOW (the biome GetBiome answers there today), through the same proven path as a
    // Chaos Hammer paint circle (BiomePaint.Regrow): natural vegetation goes through vanilla DestroyZDO, then vanilla PlaceVegetation
    // (ghost-grown when the server has no live heightmap there).
    // Never touched: anything with a creator (player pieces, ships, carts), anything inside a location's footprint, protected chunk
    // machines, skylands, portals, FAT's TileAnchors and voxel anchors (only vegetation prefabs are ever removed). Server only.
    // Skipping zones without built terrain left large areas bare, so EVERY zone is cleared. Zones whose terrain is ready grow at once; the rest go on the REGROW QUEUE, saved beside the
    // world (FiresWorldGen\<world>_regenqueue.txt) so it survives restarts, and grow as soon as their terrain is built (the server asks
    // for RegenProbe queued zones' terrain per second, and a player loading the zone builds it too). No zone stays bare: a zone that
    // loses its terrain between the clear and the growth goes back on the queue. The same queue holds zones whose NEW location
    // (ReplaceZoneLocations) waits for the zone to be loaded on the server ("loc" lines).
    internal static partial class BiomePaint
    {
        private const int RegenProbe = 24;
        private const int RegenGrowPerTick = 2;
        private const float RegenTickSeconds = 1f;
        private const float RegenSaveSeconds = 5f;
        private static readonly List<Vector2s> s_regenQueue = new List<Vector2s>();
        private static readonly HashSet<Vector2s> s_regenQueued = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_locQueue = new List<Vector2s>();
        private static float s_regenAt, s_regenSaveAt;
        private static bool s_regenDirty;
        private static World s_regenWorld;
        private static int s_regenGrownFromQueue, s_locPlacedFromQueue;

        internal static int RegenQueued => s_regenQueue.Count;

        // Thousands of per-zone lines per regen buried the logs, so per-zone RegenerateZones call lines and growth
        // lines fold into one summary per FoldSeconds. A burst's FIRST line and its LAST line are logged in full, and so is every
        // line that reports a failure (logged at once, never folded).
        private const float FoldSeconds = 3f;
        private sealed class LineFold
        {
            private readonly string _what, _aName, _bName;
            private float _windowEnds, _quietAt;
            private int _folded; private long _a, _b; private string _last;
            internal LineFold(string what, string aName, string bName) { _what = what; _aName = aName; _bName = bName; }
            internal void Log(string line, long a, long b, bool failed = false)
            {
                float now = Time.realtimeSinceStartup;
                if (failed) { FiresLogger.LogWarning(line); return; }
                if (now >= _quietAt && _folded == 0) { FiresLogger.LogInfo(line); _windowEnds = now + FoldSeconds; _quietAt = now + FoldSeconds; return; }   // first of a burst
                _folded++; _a += a; _b += b; _last = line; _quietAt = now + FoldSeconds;
            }
            // Every window: the summary; when the burst has gone quiet: its last line in full.
            internal void Flush(bool force)
            {
                if (_folded == 0) return;
                float now = Time.realtimeSinceStartup;
                bool quiet = now >= _quietAt;
                if (!force && !quiet && now < _windowEnds) return;
                FiresLogger.LogInfo($"[BiomePaint] {_what}: {_folded} more line(s) folded ({_a} {_aName}, {_b} {_bName})" + (quiet || force ? "; the last one in full:" : "."));
                if (quiet || force) FiresLogger.LogInfo(_last);
                _folded = 0; _a = _b = 0; _windowEnds = now + FoldSeconds;
            }
        }
        private static readonly LineFold s_callFold = new LineFold("RegenerateZones APPLIED calls", "cleared", "grow now");
        private static readonly LineFold s_dryFold = new LineFold("RegenerateZones DRY-RUN calls (nothing changed)", "would be cleared", "-");
        private static readonly LineFold s_growFold = new LineFold("RegenerateZones growth", "cleared", "grown again");
        internal static int LocationsQueued => s_locQueue.Count;

        private static void QueueRegen(Vector2s zone)
        {
            if (s_regenQueued.Add(zone)) { s_regenQueue.Add(zone); s_regenDirty = true; }
        }

        private static void QueueLocation(Vector2s zone)
        {
            foreach (var z in s_locQueue) if (z.x == zone.x && z.y == zone.y) return;
            s_locQueue.Add(zone);
            s_regenDirty = true;
        }

        private static bool ReadyToGrow(ZoneSystem zs, Vector2s zone) => HeightmapOf(zs, zone) != null || TerrainReady(zs, zone);

        // counts: two ints per zone in input order (duplicates dropped): [vegetation removed (dry run: would be), player pieces kept].
        internal static int[] RegrowZones(IList<Vector2s> zones, bool dryRun, out string report)
        {
            report = string.Empty;
            var zs = ZoneSystem.instance;
            if (zs == null || ZDOMan.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) { report = "not the server"; return null; }
            if (!Resolve()) { report = "ZoneSystem's internals changed shape"; return null; }
            EnsureRegenWorld();
            var list = new List<Vector2s>();
            var set = new HashSet<Vector2s>();
            foreach (var z in zones) if (set.Add(z)) list.Add(z);
            var counts = new int[list.Count * 2];
            var ready = new List<Vector2s>();
            var later = new List<Vector2s>();
            foreach (var z in list) (ReadyToGrow(zs, z) ? ready : later).Add(z);
            int removed = ClearZones(zs, list, set, counts, dryRun, out int kept);
            string label = $"FatApi.RegenerateZones ({list.Count} zone(s))";
            if (!dryRun)
            {
                if (ready.Count > 0) AddGrowth(ready, removed, label);
                foreach (var z in later) QueueRegen(z);
                SaveRegenQueue();
            }
            report = dryRun
                ? $"{label} dry run: {removed} generated object(s) would be cleared; {ready.Count} zone(s) would grow again now, {later.Count} would be queued "
                    + $"(grow when their terrain is built or someone enters); {kept} player piece(s) kept, {s_keepOut.Count} location footprint(s) left alone."
                : $"{label}: {removed} generated object(s) cleared; {ready.Count} zone(s) grow again now, {later.Count} queued (saved; {s_regenQueue.Count} in the queue, "
                    + $"each grows when its terrain is built or someone enters); {kept} player piece(s) kept.";
            // Folding could hide that every call was a dry run, so dry runs fold apart from applied calls, and each call
            // logs one Debug line with its zone(s), mode, counts and the biome its regrow will use (GetBiome at the zone centre now).
            if (list.Count <= 4)
                foreach (var z in list)
                {
                    var c = ZoneSystem.GetZonePos(z);
                    var biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(c) : Heightmap.Biome.None;
                    FiresLogger.LogDebug($"[BiomePaint] RegenerateZones zone ({z.x},{z.y}) {(dryRun ? "DRY RUN" : "applied")}: "
                        + $"{(ready.Contains(z) ? "grows now" : "queued")}, biome at its centre {biome}");
                }
            (dryRun ? s_dryFold : s_callFold).Log("[BiomePaint] " + report, removed, dryRun ? 0 : ready.Count);   // folded
            return counts;
        }

        // One zone per growth entry, so GrowPending (one entry per tick) lays one zone's vegetation per frame.
        private static void AddGrowth(List<Vector2s> zones, int cleared, string label)
        {
            foreach (var z in zones)
                s_pending.Add(new PendingGrowth { Zones = new List<Vector2s> { z }, ZoneSet = new HashSet<Vector2s> { z }, Cleared = zones.Count == 1 ? cleared : 0, Due = Time.time + GrowDelaySeconds, Label = label });
        }

        private static int ClearZones(ZoneSystem zs, List<Vector2s> list, HashSet<Vector2s> set, int[] counts, bool dryRun, out int kept)
        {
            kept = 0;
            EnsureVegetation();
            CollectKeepOutZones(zs, set);
            var found = new List<ZDO>();
            int removed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                found.Clear();
                ZDOMan.instance.FindSectorObjects(list[i], new SimulationDistance(0, 0), found);
                foreach (var zdo in found)
                {
                    if (zdo == null) continue;
                    if (ZoneModeBuilds.IsBuilt(zdo)) { if (counts != null) counts[2 * i + 1]++; kept++; continue; }
                    if (!s_vegetation.Contains(zdo.GetPrefab()) || zdo.GetLong(ZDOVars.s_creator) != 0L) continue;
                    var at = zdo.GetPosition();
                    if (InKeepOut(at) || ZoneModeChunkMachines.IsProtected(zdo) || FiresSkyIslands.TrySkylandAt(at, out _)) continue;
                    if (counts != null) counts[2 * i]++;
                    removed++;
                    if (dryRun) continue;
                    zdo.SetOwner(ZDOMan.GetSessionID());
                    ZDOMan.instance.DestroyZDO(zdo);
                }
            }
            return removed;
        }

        // From Tick, on the server.
        private static void ProcessRegenQueue()
        {
            EnsureRegenWorld();
            s_callFold.Flush(false); s_growFold.Flush(false); s_dryFold.Flush(false);
            if (Time.time >= s_regenSaveAt && s_regenDirty) { s_regenSaveAt = Time.time + RegenSaveSeconds; SaveRegenQueue(); }
            if ((s_regenQueue.Count == 0 && s_locQueue.Count == 0) || Time.time < s_regenAt) return;
            s_regenAt = Time.time + RegenTickSeconds;
            var zs = ZoneSystem.instance;
            if (zs == null || ZDOMan.instance == null || !Resolve()) return;
            // New locations waiting for their zone to be loaded here: cleared footprint, then vanilla places them.
            for (int i = s_locQueue.Count - 1; i >= 0; i--)
            {
                var zone = s_locQueue[i];
                if (!zs.m_locationInstances.TryGetValue(zone, out var instance) || instance.m_location == null || instance.m_placed)
                {
                    s_locQueue.RemoveAt(i); s_regenDirty = true;
                    continue;
                }
                if (HeightmapOf(zs, zone) == null || PlayerInside(zone) || !ResolveLocations()) continue;
                ClearFootprint(zs, zone, instance.m_location, instance.m_position);
                QueuePlacement(zs, zone);
                s_locQueue.RemoveAt(i); s_regenDirty = true;
                s_locPlacedFromQueue++;
                FiresLogger.LogInfo($"[BiomePaint] queued location {LocationName(instance.m_location)} placed in zone ({zone.x},{zone.y}) ({s_locQueue.Count} still queued).");
            }
            if (s_regenQueue.Count == 0) return;
            var ready = new List<Vector2s>();
            int probe = Mathf.Min(RegenProbe, s_regenQueue.Count);
            for (int i = 0; i < probe && ready.Count < RegenGrowPerTick; i++)   // at most RegenGrowPerTick cleared + queued per second
                if (ReadyToGrow(zs, s_regenQueue[i])) ready.Add(s_regenQueue[i]);
            if (ready.Count == 0)
            {
                var head = s_regenQueue[0];   // rotate: one slow zone never blocks the rest
                s_regenQueue.RemoveAt(0);
                s_regenQueue.Add(head);
                return;
            }
            foreach (var z in ready) { s_regenQueue.Remove(z); s_regenQueued.Remove(z); }
            s_regenDirty = true;
            var set = new HashSet<Vector2s>(ready);
            int removed = ClearZones(zs, ready, set, null, false, out _);   // anything that grew there meanwhile
            AddGrowth(ready, removed, $"FatApi.RegenerateZones queue ({ready.Count} zone(s))");
            foreach (var z in ready)
                FiresLogger.LogDebug($"[BiomePaint] regrow queue: zone ({z.x},{z.y}) leaves the queue to grow, biome at its centre {(WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(ZoneSystem.GetZonePos(z)) : Heightmap.Biome.None)}");
            s_regenGrownFromQueue += ready.Count;
            FiresLogger.LogInfo($"[BiomePaint] regrow queue: {ready.Count} zone(s) growing, {s_regenQueue.Count} still queued ({s_regenGrownFromQueue} done from the queue this session).");
        }

        // ── The queue file: "veg x y" / "loc x y" lines under a "world <uid> <seed>" header ──
        private static string RegenQueuePath() => FiresWorldRecords.PathFor(ZNet.World?.m_name ?? "world", "_regenqueue.txt");

        private static void EnsureRegenWorld()
        {
            var world = ZNet.World;
            if (ReferenceEquals(world, s_regenWorld)) return;
            s_regenWorld = world;
            AbortJob("the world changed");
            s_regenQueue.Clear(); s_regenQueued.Clear(); s_locQueue.Clear(); s_regenDirty = false;
            if (world == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string path = RegenQueuePath();
            if (!File.Exists(path)) return;
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != $"world {world.m_uid} {world.m_seed}")
                {
                    FiresLogger.LogWarning($"[BiomePaint] {Path.GetFileName(path)} belongs to another world by this name; not used.");
                    return;
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    var p = lines[i].Split(' ');
                    if (p.Length != 3 || !int.TryParse(p[1], out int x) || !int.TryParse(p[2], out int y)) continue;
                    var zone = new Vector2s(x, y);
                    if (p[0] == "veg") { if (s_regenQueued.Add(zone)) s_regenQueue.Add(zone); }
                    else if (p[0] == "loc") s_locQueue.Add(zone);
                }
                FiresLogger.LogInfo($"[BiomePaint] regrow queue read for '{world.m_name}': {s_regenQueue.Count} zone(s) to grow, {s_locQueue.Count} location(s) to place.");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] {Path.GetFileName(path)} could not be read ({ex.Message})."); }
        }

        private static void SaveRegenQueue()
        {
            var world = ZNet.World;
            if (world == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            s_regenDirty = false;
            string path = RegenQueuePath();
            try
            {
                if (s_regenQueue.Count == 0 && s_locQueue.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string> { $"world {world.m_uid} {world.m_seed}" };
                foreach (var z in s_regenQueue) lines.Add($"veg {z.x} {z.y}");
                foreach (var z in s_locQueue) lines.Add($"loc {z.x} {z.y}");
                string temp = path + ".tmp";
                File.WriteAllLines(temp, lines);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[BiomePaint] the regrow queue could not be saved ({ex.Message})."); }
        }

        private static void CollectKeepOutZones(ZoneSystem zones, HashSet<Vector2s> set)
        {
            s_keepOut.Clear();
            var seen = new HashSet<Vector2s>();
            foreach (var zone in set)
                for (int z = zone.y - 1; z <= zone.y + 1; z++)
                    for (int x = zone.x - 1; x <= zone.x + 1; x++)
                    {
                        var key = new Vector2s(x, z);
                        if (!seen.Add(key)) continue;
                        if (!zones.m_locationInstances.TryGetValue(key, out var instance) || instance.m_location == null) continue;
                        float radius = Mathf.Max(instance.m_location.m_exteriorRadius, instance.m_location.m_interiorRadius) + LocationMargin;
                        s_keepOut.Add((instance.m_position, radius));
                    }
        }
    }
}
