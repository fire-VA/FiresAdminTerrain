using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using VerdantsAscent.Terrain.BcImport;
using VerdantsAscent.Terrain.Zones;

namespace VerdantsAscent.Terrain.Studio
{
    // 0.2.1245 (Fire 10-29 "Studio FAT first", step 2 after terrain; plan §0d.4a/§0d.4b): the World Studio's location place / remove
    // with undo. Place mirrors what vanilla ZoneSystem.PlaceLocations does for one zone (ground height, rotation, the zone seed,
    // SpawnLocation in Full mode, the instance marked placed, icons), at the Studio's spot and optional fixed rotation; its
    // snapshot is every ZDO the spawn created (prefab + position: ZDOIDs are not stable across loads), and undo destroys them and
    // drops the instance again. Remove takes away the parts the biome swap takes (BiomePaint.StudioPartPrefabs: the prefab's
    // networked parts, its spawners' creatures, the proxy, the dungeon interior) plus creator-0 pieces in its radius (EWD
    // blueprint locations); players, tombstones, tames, player-built and protected objects are kept. Its snapshot is each removed
    // ZDO's full network data (ZDO.Serialize), and undo creates them again (new ZDOIDs, same data) and re-registers the instance.
    // One location per zone, as vanilla. Host only.
    internal static class FatStudioLocations
    {
        internal const string Kind = "location";
        internal const int Version = 2;   // 0.2.1258: v2 place snapshots also hold what stood there before (v1 still restores)
        private const byte OpPlace = 1, OpRemove = 2;
        private const float SamePos = 0.05f, SameHandle = 1f, LocationMargin = 2f, InteriorFloorY = 2500f;
        private const int MaxObjects = 20000;
        private const string Tag = "[FatStudio]";
        private static readonly int PlayerHash = "Player".GetStableHashCode();
        private static readonly int TombstoneHash = "Player_tombstone".GetStableHashCode();

        internal static bool Place(FatLocationArgs a, out FatLocationHandle handle, out FatSnapshot snapshot, out string reason)
        {
            handle = null; snapshot = null;
            if (!Ready(out var zs, out reason)) return false;
            if (a == null || string.IsNullOrEmpty(a.Name)) { reason = "bad args: no location name"; return false; }
            var loc = Find(zs, a.Name);
            if (loc == null) { reason = $"bad args: no enabled location named '{a.Name}'"; return false; }
            Vector2s zone = ZoneSystem.GetZone(a.Position);
            if (!zs.IsZoneGenerated(zone)) { reason = $"not loaded: zone {zone.x},{zone.y} is not generated"; return false; }
            if (zs.m_locationInstances.TryGetValue(zone, out var had))
            { reason = $"zone {zone.x},{zone.y} already has {BcRegenCommand.LocName(had.m_location)} at ({had.m_position.x:0},{had.m_position.z:0}): remove it first (one location per zone)"; return false; }
            if (!zs.GetGroundHeight(a.Position, out float ground)) { reason = $"not loaded: no ground at ({a.Position.x:0},{a.Position.z:0})"; return false; }
            if (!zs.PokeCanSpawnLocation(loc, true)) { reason = $"loading: {BcRegenCommand.LocName(loc)}'s prefab is loading, retry in a moment"; return false; }

            Vector3 pos = new Vector3(a.Position.x, loc.m_snapToWater ? zs.m_waterLevel : ground, a.Position.z);
            int seed = a.Seed != 0 ? a.Seed : WorldGenerator.instance.GetSeed() + zone.x * 4271 + zone.y * 9187;
            Quaternion rot = Rotation(zs, loc, pos, seed, a.RotY);

            var beforeSet = new HashSet<ZDO>(Around(zone));
            try
            {
                zs.RegisterLocation(loc, pos, true);
                zs.SpawnLocation(loc, seed, pos, rot, ZoneSystem.SpawnMode.Full, new List<GameObject>(), false);
            }
            catch (Exception ex)
            {
                zs.m_locationInstances.Remove(zone);
                reason = $"failed: SpawnLocation {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            if (zs.m_locationInstances.TryGetValue(zone, out var inst)) { inst.m_placed = true; inst.m_position = pos; zs.m_locationInstances[zone] = inst; }
            if (loc.m_iconPlaced) Icons(zs);

            var created = new List<ZDO>();
            foreach (var z in Around(zone)) if (!beforeSet.Contains(z)) created.Add(z);
            int stamped = 0;
            if (a.Creator != 0L)
            {
                stamped = Stamp(created, a.Creator, zone);
                if (FiresAdminTerrain.Instance != null)
                    FiresAdminTerrain.Instance.StartCoroutine(StampLate(zone, pos, Mathf.Max(loc.m_exteriorRadius, loc.m_interiorRadius) + LocationMargin, new HashSet<ZDO>(beforeSet), a.Creator, BcRegenCommand.LocName(loc)));
            }
            handle = new FatLocationHandle { Name = BcRegenCommand.LocName(loc), NameHash = BcRegenCommand.LocName(loc).GetStableHashCode(), Position = pos };
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(OpPlace); WriteHead(w, handle.Name, pos, zone);
                    w.Write(created.Count);
                    foreach (var z in created) { w.Write(z.GetPrefab()); WriteV3(w, z.GetPosition()); }
                    // 0.2.1258 (round O: undo left a part the proxy spawned AFTER the place): what stood in the zones before, by prefab +
                    // position (0.1 m), so the undo also takes away the parts that appeared later
                    w.Write(beforeSet.Count);
                    foreach (var z in beforeSet) { w.Write(z.GetPrefab()); w.Write(Mathf.RoundToInt(z.GetPosition().x * 10f)); w.Write(Mathf.RoundToInt(z.GetPosition().z * 10f)); }
                }
                snapshot = Snap(ms.ToArray());
            }
            reason = $"ok: {handle.Name} placed at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) rot {rot.eulerAngles.y:0} seed {seed}: {created.Count} object(s)" + (a.Creator != 0L ? $", creator {a.Creator} on {stamped} piece(s)" : ", world-owned");
            FiresLogger.LogDebug($"{Tag} LocationPlace {reason}; snapshot {snapshot.Id}");
            return true;
        }

        internal static bool Remove(FatLocationHandle h, out FatSnapshot snapshot, out string reason)
        {
            snapshot = null;
            if (!Ready(out var zs, out reason)) return false;
            if (h == null) { reason = "bad args: no handle"; return false; }
            Vector2s zone = ZoneSystem.GetZone(h.Position);
            if (!zs.m_locationInstances.TryGetValue(zone, out var inst) || inst.m_location == null
                || BcRegenCommand.LocName(inst.m_location).GetStableHashCode() != h.NameHash || FlatDist(inst.m_position, h.Position) > SameHandle)
            { reason = $"not found: no {h} in zone {zone.x},{zone.y}"; return false; }

            var loc = inst.m_location;
            var parts = BiomePaint.StudioPartPrefabs(zs, loc, out bool dungeon);
            float reach = Mathf.Max(loc.m_exteriorRadius, loc.m_interiorRadius) + LocationMargin;
            var doomed = new List<ZDO>();
            int kept = 0;
            foreach (var z in Around(zone))
            {
                int prefab = z.GetPrefab();
                var at = z.GetPosition();
                bool inside = dungeon && at.y > InteriorFloorY && ZoneSystem.GetZone(at) == zone;
                bool part = inside || ((parts.Contains(prefab) || IsPiece(prefab)) && FlatDist(at, inst.m_position) <= reach);
                if (!part) continue;
                if (prefab == PlayerHash || prefab == TombstoneHash || Built(z, zone) || z.GetBool(ZDOVars.s_tamed)
                    || ZoneModeChunkMachines.IsProtected(z)) { kept++; continue; }
                doomed.Add(z);
                if (doomed.Count > MaxObjects) { reason = $"tooLarge: more than {MaxObjects} objects"; return false; }
            }
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(OpRemove); WriteHead(w, BcRegenCommand.LocName(loc), inst.m_position, zone);
                    w.Write(inst.m_placed);
                    w.Write(doomed.Count);
                    foreach (var z in doomed)
                    {
                        var pkg = new ZPackage();
                        z.Serialize(pkg);
                        byte[] data = pkg.GetArray();
                        w.Write(z.GetPrefab()); WriteV3(w, z.GetPosition()); WriteV3(w, z.GetRotation().eulerAngles);
                        w.Write(data.Length); w.Write(data);
                    }
                }
                snapshot = Snap(ms.ToArray());
            }
            long me = ZDOMan.GetSessionID();
            foreach (var z in doomed) { z.SetOwner(me); ZDOMan.instance.DestroyZDO(z); }
            zs.m_locationInstances.Remove(zone);
            if (loc.m_iconPlaced || loc.m_iconAlways) Icons(zs);
            reason = $"ok: {BcRegenCommand.LocName(loc)} removed from zone {zone.x},{zone.y}: {doomed.Count} object(s), {kept} kept (players / tombstones / tames / built / protected)";
            FiresLogger.LogDebug($"{Tag} LocationRemove {reason}; snapshot {snapshot.Id} {snapshot.Bytes.Length} bytes");
            return true;
        }

        internal static bool Restore(FatSnapshot s, out string reason)
        {
            if (!Ready(out var zs, out reason)) return false;
            if (s == null || s.Bytes == null || s.Kind != Kind || (s.Version != 1 && s.Version != 2)) { reason = $"bad snapshot: kind {s?.Kind} v{s?.Version}"; return false; }
            if (s.Hash != null && !SameBytes(Sha(s.Bytes), s.Hash)) { reason = "bad snapshot: hash mismatch"; return false; }
            try
            {
                using (var r = new BinaryReader(new MemoryStream(s.Bytes)))
                {
                    byte op = r.ReadByte();
                    ReadHead(r, out string name, out Vector3 pos, out Vector2s zone);
                    return op == OpPlace ? UndoPlace(zs, r, name, pos, zone, s.Version, out reason)
                        : op == OpRemove ? UndoRemove(zs, r, name, pos, zone, out reason)
                        : Fail($"bad snapshot: op {op}", out reason);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException) { reason = $"bad snapshot: {ex.GetType().Name}"; return false; }
        }

        private static bool UndoPlace(ZoneSystem zs, BinaryReader r, string name, Vector3 pos, Vector2s zone, int version, out string reason)
        {
            if (!zs.m_locationInstances.TryGetValue(zone, out var inst) || inst.m_location == null
                || BcRegenCommand.LocName(inst.m_location) != name || FlatDist(inst.m_position, pos) > SameHandle)
            { reason = $"stale: {name} is no longer at ({pos.x:0},{pos.z:0}) in zone {zone.x},{zone.y}"; return false; }
            int n = r.ReadInt32();
            if (n < 0 || n > MaxObjects) return Fail($"bad snapshot: {n} objects", out reason);
            var want = new List<(int prefab, Vector3 at)>(n);
            for (int i = 0; i < n; i++) want.Add((r.ReadInt32(), ReadV3(r)));
            var near = Around(zone);
            long me = ZDOMan.GetSessionID();
            int gone = 0, missing = 0;
            foreach (var (prefab, at) in want)
            {
                ZDO hit = null;
                foreach (var z in near) if (z.IsValid() && z.GetPrefab() == prefab && (z.GetPosition() - at).sqrMagnitude <= SamePos * SamePos) { hit = z; break; }
                if (hit == null) { missing++; continue; }
                hit.SetOwner(me); ZDOMan.instance.DestroyZDO(hit); gone++;
            }
            int late = 0;
            if (version >= 2)
            {
                int m = r.ReadInt32();
                if (m < 0 || m > 200000) return Fail($"bad snapshot: {m} prior objects", out reason);
                var prior = new HashSet<(int, int, int)>();
                for (int i = 0; i < m; i++) prior.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                var parts = BiomePaint.StudioPartPrefabs(zs, inst.m_location, out bool dungeon);
                float reach = Mathf.Max(inst.m_location.m_exteriorRadius, inst.m_location.m_interiorRadius) + LocationMargin;
                foreach (var z in Around(zone))
                {
                    if (!z.IsValid()) continue;
                    int prefab = z.GetPrefab(); var at = z.GetPosition();
                    bool inside = dungeon && at.y > InteriorFloorY && ZoneSystem.GetZone(at) == zone;
                    if (!inside && !((parts.Contains(prefab) || IsPiece(prefab)) && FlatDist(at, inst.m_position) <= reach)) continue;
                    if (prefab == PlayerHash || prefab == TombstoneHash || Built(z, zone)) continue;
                    if (prior.Contains((prefab, Mathf.RoundToInt(at.x * 10f), Mathf.RoundToInt(at.z * 10f)))) continue;
                    z.SetOwner(me); ZDOMan.instance.DestroyZDO(z); late++;
                }
                // 0.2.1261 (round Q: a Spawner_Boar stood at -376.3,180.9 after a Runestone_Boars place undo, in neither the removed
                // list nor the late parts): 5 s later, Debug-list every creator-0 object within the reach that was not there before the
                // place and is still there, with whether it is in the location's part list. Evidence only, nothing is removed here.
                if (FiresAdminTerrain.Instance != null)
                    FiresAdminTerrain.Instance.StartCoroutine(LateCheck(name, zone, inst.m_position, reach, prior, parts));
            }
            bool icon = inst.m_location.m_iconPlaced || inst.m_location.m_iconAlways;
            zs.m_locationInstances.Remove(zone);
            if (icon) Icons(zs);
            reason = $"ok: {name} place undone in zone {zone.x},{zone.y}: {gone} object(s) removed" + (late > 0 ? $", {late} part(s) spawned after the place removed" : "") + (missing > 0 ? $", {missing} already gone" : "");
            FiresLogger.LogDebug($"{Tag} Restore (location place) {reason}");
            return true;
        }

        private static System.Collections.IEnumerator LateCheck(string name, Vector2s zone, Vector3 at, float reach, HashSet<(int, int, int)> prior, ICollection<int> parts)
        {
            yield return new WaitForSecondsRealtime(5f);
            if (ZDOMan.instance == null || ZNetScene.instance == null) yield break;
            var found = new List<string>();
            foreach (var z in Around(zone))
            {
                if (!z.IsValid()) continue;
                int prefab = z.GetPrefab(); var p = z.GetPosition();
                if (prefab == PlayerHash || prefab == TombstoneHash || Built(z, zone) || FlatDist(p, at) > reach) continue;
                if (prior.Contains((prefab, Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.z * 10f)))) continue;
                var go = ZNetScene.instance.GetPrefab(prefab);
                if (go != null && (go.GetComponent<Character>() != null || go.GetComponent<ItemDrop>() != null)) continue;
                found.Add($"{(go != null ? go.name : prefab.ToString())}@{p.x:0.0},{p.z:0.0} (part list: {(parts != null && parts.Contains(prefab) ? "yes" : "no")}, uid {z.m_uid})");
            }
            FiresLogger.LogDebug($"{Tag} location undo late check {name} zone {zone.x},{zone.y} +5 s: {found.Count} new object(s) within {reach:0} m" + (found.Count > 0 ? ": " + string.Join("; ", found.Count > 12 ? found.GetRange(0, 12) : found) : ""));
        }

        private static bool UndoRemove(ZoneSystem zs, BinaryReader r, string name, Vector3 pos, Vector2s zone, out string reason)
        {
            if (zs.m_locationInstances.TryGetValue(zone, out var other) && other.m_location != null)
            { reason = $"stale: zone {zone.x},{zone.y} now has {BcRegenCommand.LocName(other.m_location)}"; return false; }
            var loc = Find(zs, name);
            if (loc == null) return Fail($"unsupported: location '{name}' is not in this world's location list", out reason);
            bool placed = r.ReadBoolean();
            int n = r.ReadInt32();
            if (n < 0 || n > MaxObjects) return Fail($"bad snapshot: {n} objects", out reason);
            long me = ZDOMan.GetSessionID();
            int made = 0;
            for (int i = 0; i < n; i++)
            {
                int prefab = r.ReadInt32();
                Vector3 at = ReadV3(r), euler = ReadV3(r);
                int len = r.ReadInt32();
                if (len < 0 || len > 1024 * 1024) return Fail("bad snapshot: object data too large", out reason);
                byte[] data = r.ReadBytes(len);
                var z = ZDOMan.instance.CreateNewZDO(at, prefab);
                z.Deserialize(new ZPackage(data));
                z.SetOwner(me);
                z.SetPosition(at);
                z.SetRotation(Quaternion.Euler(euler));
                made++;
            }
            zs.RegisterLocation(loc, pos, true);
            if (zs.m_locationInstances.TryGetValue(zone, out var inst)) { inst.m_placed = placed; inst.m_position = pos; zs.m_locationInstances[zone] = inst; }
            if (loc.m_iconPlaced || loc.m_iconAlways) Icons(zs);
            reason = $"ok: {name} remove undone in zone {zone.x},{zone.y}: {made} object(s) made again (same data, new ids)";
            FiresLogger.LogDebug($"{Tag} Restore (location remove) {reason}");
            return true;
        }

        // The placed location instances within radius of centre (radius <= 0: the one in centre's zone).
        internal static FatLocationHandle[] List(Vector3 centre, float radius)
        {
            var zs = ZoneSystem.instance;
            var list = new List<FatLocationHandle>();
            if (zs == null) return list.ToArray();
            Vector2s cz = ZoneSystem.GetZone(centre);
            foreach (var kv in zs.m_locationInstances)
            {
                var inst = kv.Value;
                if (inst.m_location == null || !inst.m_placed) continue;
                if (radius <= 0f ? kv.Key != cz : FlatDist(inst.m_position, centre) > radius) continue;
                string n = BcRegenCommand.LocName(inst.m_location);
                list.Add(new FatLocationHandle { Name = n, NameHash = n.GetStableHashCode(), Position = inst.m_position });
            }
            return list.ToArray();
        }

        internal static string[] Names()
        {
            var zs = ZoneSystem.instance;
            var names = new SortedSet<string>(StringComparer.Ordinal);
            if (zs?.m_locations != null) foreach (var l in zs.m_locations) if (l != null && l.m_enable) names.Add(BcRegenCommand.LocName(l));
            return new List<string>(names).ToArray();
        }

        // The sum of the data revisions of the location's objects plus its instance (0 = no such location here).
        internal static uint Revision(FatLocationHandle h)
        {
            var zs = ZoneSystem.instance;
            if (zs == null || h == null) return 0u;
            Vector2s zone = ZoneSystem.GetZone(h.Position);
            if (!zs.m_locationInstances.TryGetValue(zone, out var inst) || inst.m_location == null
                || BcRegenCommand.LocName(inst.m_location).GetStableHashCode() != h.NameHash) return 0u;
            // 0.2.1255 (round N: "stale: location rev is 2642820529" even at 1 m: after a place the location's proxy keeps spawning
            // its contents, so any revision over its OBJECTS grows on its own). The revision is the location INSTANCE record: name,
            // position (0.1 m) and placed flag. It changes only when the location itself is placed, removed, replaced or moved.
            FiresLogger.LogDebug($"{Tag} LocationRevision {h}: instance at ({inst.m_position.x:0.0},{inst.m_position.y:0.0},{inst.m_position.z:0.0}) placed {inst.m_placed}");   // 0.2.1258: what the revision is made of
            uint rev = unchecked((uint)h.NameHash * 2654435761u ^ (uint)Mathf.RoundToInt(inst.m_position.x * 10f) * 40503u
                ^ (uint)Mathf.RoundToInt(inst.m_position.z * 10f) * 1103515245u ^ (inst.m_placed ? 0x9E3779B9u : 0u));
            return rev == 0u ? 1u : rev;
        }

        // `fat_studio_location_test [name]` (host, admin): in the zone 3 zones in front of you (it must have no location), place
        // the location (default Runestone_Boars) → its objects exist → undo → 0 left; place again → remove → undo the remove →
        // same object count back → undo the place. Every line says why it passed or failed.
        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("fat_studio_location_test", "[location] - host: Studio location place / undo / remove / undo round trip in a free zone in front of you",
                args =>
                {
                    var me = Player.m_localPlayer;
                    if (me == null || FiresAdminTerrain.Instance == null) { args.Context?.AddString($"{Tag} no player"); return; }
                    string name = args.Length > 1 ? args[1] : "Runestone_Boars";
                    Vector3 at = me.transform.position + me.transform.forward * (ZoneSystem.c_ZoneSize * 1.5f);
                    FiresAdminTerrain.Instance.StartCoroutine(SelfTest(name, at, args.Context));
                }, isCheat: true);
        }

        private static System.Collections.IEnumerator SelfTest(string name, Vector3 at, Terminal ctx)
        {
            int pass = 0, fail = 0;
            void Say(string line) { FiresLogger.LogInfo($"{Tag} {line}"); ctx?.AddString($"{Tag} {line}"); }
            Vector2s zone = ZoneSystem.GetZone(at);
            var args = new FatLocationArgs { Name = name, Position = ZoneSystem.GetZonePos(zone), RotY = 90f };
            FatLocationHandle h = null; FatSnapshot ps = null; string why = null;
            for (float until = Time.realtimeSinceStartup + 30f; Time.realtimeSinceStartup < until;)
            {
                if (Place(args, out h, out ps, out why)) break;
                if (!why.StartsWith("loading:", StringComparison.Ordinal)) break;
                yield return null;
            }
            if (ps == null) { Say($"FAIL place: {why}"); Say($"fat_studio_location_test done: 0 PASS, 1 FAIL"); yield break; }
            yield return new WaitForSecondsRealtime(1f);
            int placedCount = Count(zone, h);
            if (placedCount > 0) { pass++; Say($"PASS place: {why}; {placedCount} object(s) in its radius"); } else { fail++; Say($"FAIL place: nothing in its radius ({why})"); }
            if (Restore(ps, out why) && Count(zone, h) == 0 && List(h.Position, 0f).Length == 0) { pass++; Say($"PASS undo place: {why}; 0 left"); }
            else { fail++; Say($"FAIL undo place: {why}; {Count(zone, h)} left"); }
            yield return new WaitForSecondsRealtime(1f);
            if (!Place(args, out h, out ps, out why)) { fail++; Say($"FAIL place again: {why}"); }
            else
            {
                yield return new WaitForSecondsRealtime(1f);
                int n = Count(zone, h);
                if (!Remove(h, out var rs, out why)) { fail++; Say($"FAIL remove: {why}"); }
                else
                {
                    yield return new WaitForSecondsRealtime(1f);
                    if (Count(zone, h) == 0) { pass++; Say($"PASS remove: {why}"); } else { fail++; Say($"FAIL remove: {Count(zone, h)} left ({why})"); }
                    if (Restore(rs, out why) && List(h.Position, 0f).Length == 1) { yield return new WaitForSecondsRealtime(1f); int back = Count(zone, h); if (back == n) { pass++; Say($"PASS undo remove: {why}; {back} of {n} back"); } else { fail++; Say($"FAIL undo remove: {back} of {n} back ({why})"); } }
                    else { fail++; Say($"FAIL undo remove: {why}"); }
                }
                if (Restore(ps, out why)) Say($"cleanup: {why}"); else Say($"cleanup FAILED: {why} (remove {h} by hand)");
            }
            Say($"fat_studio_location_test done: {pass} PASS, {fail} FAIL ({name} in zone {zone.x},{zone.y})");
        }

        private static int Count(Vector2s zone, FatLocationHandle h)
        {
            var zs = ZoneSystem.instance;
            var loc = zs != null ? Find(zs, h.Name) : null;
            if (loc == null) return 0;
            var parts = BiomePaint.StudioPartPrefabs(zs, loc, out bool dungeon);
            float reach = Mathf.Max(loc.m_exteriorRadius, loc.m_interiorRadius) + LocationMargin;
            int n = 0;
            foreach (var z in Around(zone))
            {
                var at = z.GetPosition();
                if ((dungeon && at.y > InteriorFloorY && ZoneSystem.GetZone(at) == zone)
                    || ((parts.Contains(z.GetPrefab()) || IsPiece(z.GetPrefab())) && z.GetLong(ZDOVars.s_creator) == 0L && FlatDist(at, h.Position) <= reach)) n++;
            }
            return n;
        }

        // ── helpers ──
        private static bool Ready(out ZoneSystem zs, out string reason)
        {
            zs = ZoneSystem.instance;
            reason = null;
            if (ZNet.instance == null || zs == null || ZDOMan.instance == null || WorldGenerator.instance == null) { reason = "no world"; return false; }
            if (!ZNet.instance.IsServer()) { reason = "not host: Studio locations run on the server/host only"; return false; }
            return true;
        }

        private static ZoneSystem.ZoneLocation Find(ZoneSystem zs, string name)
        {
            if (zs.m_locations == null) return null;
            foreach (var l in zs.m_locations) if (l != null && l.m_enable && l.m_prefab.IsValid && BcRegenCommand.LocName(l) == name) return l;
            foreach (var l in zs.m_locations) if (l != null && l.m_enable && l.m_prefab.IsValid && (l.m_name == name || l.m_prefab.Name == name)) return l;
            return null;
        }

        private static Quaternion Rotation(ZoneSystem zs, ZoneSystem.ZoneLocation loc, Vector3 pos, int seed, float rotY)
        {
            if (!float.IsNaN(rotY)) return Quaternion.Euler(0f, rotY, 0f);
            if (loc.m_slopeRotation)
            {
                zs.GetTerrainDelta(pos, loc.m_exteriorRadius, out _, out Vector3 slope);
                var dir = new Vector3(slope.x, 0f, slope.z);
                if (dir.sqrMagnitude > 1e-6f) { var e = Quaternion.LookRotation(dir.normalized).eulerAngles; return Quaternion.Euler(0f, Mathf.Round(e.y / 22.5f) * 22.5f, 0f); }
            }
            if (!loc.m_randomRotation) return Quaternion.identity;
            var state = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            var q = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 16) * 22.5f, 0f);
            UnityEngine.Random.state = state;
            return q;
        }

        // Every ZDO in the zone and its 8 neighbours (a location's radius can cross the zone edge; its dungeon is above the zone).
        private static List<ZDO> Around(Vector2s zone)
        {
            var all = new List<ZDO>();
            var one = new List<ZDO>();
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    one.Clear();
                    ZDOMan.instance.FindSectorObjects(new Vector2s(zone.x + dx, zone.y + dz), new SimulationDistance(0, 0), one);
                    foreach (var z in one) if (z != null && z.IsValid()) all.Add(z);
                }
            return all;
        }

        // 0.2.1262 (Fire 10-29: a World / Me choice per Studio placement; FGC 0.8.4 placeAs): a location placed with a Creator has that
        // creator on every Piece it spawns (late parts too), plus this key = the zone it belongs to, so the place's undo and a later
        // remove still take those parts (both otherwise keep creator != 0 objects as player-built).
        private static readonly int StudioLocKey = "fat_studio_loc".GetStableHashCode();
        private static int LocId(Vector2s zone) => (zone.x << 16) | (zone.y & 0xFFFF);
        private static bool Ours(ZDO z, Vector2s zone) => z.GetLong(ZDOVars.s_creator) != 0L && z.GetInt(StudioLocKey, int.MinValue) == LocId(zone);
        private static bool Built(ZDO z, Vector2s zone) => z.GetLong(ZDOVars.s_creator) != 0L && !Ours(z, zone);

        private static int Stamp(IEnumerable<ZDO> zdos, long creator, Vector2s zone)
        {
            int n = 0; long me = ZDOMan.GetSessionID();
            foreach (var z in zdos)
            {
                if (!z.IsValid() || !IsPiece(z.GetPrefab()) || z.GetLong(ZDOVars.s_creator) != 0L) continue;
                if (!z.IsOwner()) z.SetOwner(me);
                z.Set(ZDOVars.s_creator, creator); z.Set(StudioLocKey, LocId(zone)); n++;
            }
            return n;
        }

        // the proxy spawns some parts after the place returns: for 10 s, stamp every new piece within the location's reach
        private static System.Collections.IEnumerator StampLate(Vector2s zone, Vector3 at, float reach, HashSet<ZDO> before, long creator, string name)
        {
            int total = 0;
            for (int i = 0; i < 10; i++)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (ZDOMan.instance == null || !ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out var inst) || FlatDist(inst.m_position, at) > SameHandle) yield break;   // undone / removed
                var late = new List<ZDO>();
                foreach (var z in Around(zone)) if (!before.Contains(z) && FlatDist(z.GetPosition(), at) <= reach) late.Add(z);
                total += Stamp(late, creator, zone);
            }
            FiresLogger.LogDebug($"{Tag} LocationPlace {name}: creator {creator} stamped on {total} late part piece(s) in 10 s");
        }

        private static readonly Dictionary<int, bool> s_piece = new Dictionary<int, bool>();
        private static bool IsPiece(int prefab)
        {
            if (s_piece.TryGetValue(prefab, out bool is_)) return is_;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            is_ = go != null && go.GetComponent<Piece>() != null;
            s_piece[prefab] = is_;
            return is_;
        }

        // Creatures, dropped items and free physics objects move by themselves: never part of a location's revision.
        private static readonly Dictionary<int, bool> s_moves = new Dictionary<int, bool>();
        private static bool Moves(int prefab)
        {
            if (s_moves.TryGetValue(prefab, out bool m)) return m;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            var rb = go != null ? go.GetComponent<Rigidbody>() : null;
            m = go == null || go.GetComponent<Character>() != null || go.GetComponent<ItemDrop>() != null || (rb != null && !rb.isKinematic);
            s_moves[prefab] = m;
            return m;
        }

        private static void Icons(ZoneSystem zs)
        {
            try { zs.SendLocationIcons(ZRoutedRpc.Everybody); }
            catch (Exception ex) { FiresLogger.LogWarning($"{Tag} location icons not sent: {ex.Message}"); }
        }

        private static float FlatDist(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        private static void WriteHead(BinaryWriter w, string name, Vector3 pos, Vector2s zone) { w.Write(name); WriteV3(w, pos); w.Write((int)zone.x); w.Write((int)zone.y); }
        private static void ReadHead(BinaryReader r, out string name, out Vector3 pos, out Vector2s zone) { name = r.ReadString(); pos = ReadV3(r); zone = new Vector2s(r.ReadInt32(), r.ReadInt32()); }
        private static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static FatSnapshot Snap(byte[] bytes) => new FatSnapshot { Id = Guid.NewGuid().ToString("N"), Kind = Kind, Version = Version, Bytes = bytes, Hash = Sha(bytes) };
        private static byte[] Sha(byte[] data) { using (var sha = SHA256.Create()) return sha.ComputeHash(data); }
        private static bool SameBytes(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private static bool Fail(string why, out string reason) { reason = why; return false; }
    }
}
