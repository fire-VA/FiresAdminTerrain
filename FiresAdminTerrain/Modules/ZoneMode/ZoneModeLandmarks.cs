using System;
using System.Collections.Generic;
using System.IO;

namespace VerdantsAscent.Terrain.Zones
{
    // ── WHAT A PARKED CHUNK PROMISES ────────────────────────────────────────
    // Two things a converted world holds can be reached from outside the chunk they stand in, so their chunk has to be
    // there before anyone arrives:
    //
    //   PORTALS — a pair is two portals carrying one tag, and the other half almost always stands in a chunk nobody has
    //   opened. When a zone returns, every tagged portal it brings back pulls its partner's chunk back with it, so a
    //   portal a player finds always leads somewhere. Untagged portals are left alone: vanilla pairs those with any
    //   other untagged portal, which names no particular chunk.
    //
    //   WAYSHRINES SET TO ALWAYS SHOW — a shrine carrying FiresWayshrines' `VAWayshrine_autoDiscover` stands on every
    //   player's map from the first minute, so its chunk is opened as soon as this index knows about it. The key is
    //   read straight off the ZDO; nothing here references that mod.
    //
    // What each parked zone holds is written beside the park index when the world is converted, which costs nothing —
    // the objects are in hand. A world converted by an earlier build has no such file, so the park is read again here,
    // a budget of objects a frame, and the answer is written for every session after.
    internal static class ZoneModeLandmarks
    {
        private const string IndexFileName = "landmarks.bin";
        private const uint IndexFileMagic = 0x444E414Cu;
        private const byte FileFormat = 1;
        private const int ObjectsReadPerTick = 3000;

        private static readonly int AutoDiscoverHash = "VAWayshrine_autoDiscover".GetStableHashCode();

        private struct Held
        {
            internal string[] PortalTags;
            internal bool Wayshrine;
        }

        private struct Follow
        {
            internal Vector2s Zone;
            internal Vector2s From;
            internal string Tag;
        }

        private static readonly Dictionary<Vector2s, Held> s_byZone = new Dictionary<Vector2s, Held>();
        private static readonly Dictionary<string, List<Vector2s>> s_zonesByTag = new Dictionary<string, List<Vector2s>>();
        private static readonly List<Follow> s_toFollow = new List<Follow>();
        private static readonly List<KeyValuePair<string, Vector2s>> s_waiting = new List<KeyValuePair<string, Vector2s>>();

        private static List<Vector2s> s_unread;
        private static string s_folder;
        private static long s_worldUid;
        private static bool s_ready;
        private static bool s_raised;

        internal static void Reset()
        {
            s_byZone.Clear();
            s_zonesByTag.Clear();
            s_toFollow.Clear();
            s_waiting.Clear();
            s_unread = null;
            s_folder = null;
            s_worldUid = 0L;
            s_ready = false;
            s_raised = false;
        }

        internal static void OnWorldInitialize(string folder, long worldUid)
        {
            Reset();
            s_folder = folder;
            s_worldUid = worldUid;
            s_ready = TryLoad();
        }

        // Conversion: the zone's objects are still in hand, so what it holds is read straight off them.
        internal static void Record(Vector2s zone, List<ZDO> objects, List<ZDO> portals)
        {
            var tags = new List<string>();
            bool wayshrine = false;
            foreach (var zdo in objects) wayshrine |= IsAlwaysShown(zdo);
            foreach (var zdo in portals)
            {
                wayshrine |= IsAlwaysShown(zdo);
                AddTag(tags, zdo);
            }
            if (tags.Count > 0 || wayshrine) Adopt(zone, tags.ToArray(), wayshrine);
        }

        internal static void Write(string folder)
        {
            string path = Path.Combine(folder, IndexFileName);
            string temp = path + ".tmp";
            try
            {
                using (var writer = new BinaryWriter(File.Create(temp)))
                {
                    writer.Write(IndexFileMagic);
                    writer.Write(FileFormat);
                    writer.Write(s_worldUid);
                    writer.Write(s_byZone.Count);
                    foreach (var pair in s_byZone)
                    {
                        writer.Write((short)pair.Key.x);
                        writer.Write((short)pair.Key.y);
                        writer.Write(pair.Value.Wayshrine);
                        writer.Write(pair.Value.PortalTags.Length);
                        foreach (string tag in pair.Value.PortalTags) writer.Write(tag);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                s_ready = true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[FiresZones] could not write what the parked zones hold: {ex.Message}");
            }
        }

        // A zone came back: its own portals are live now, and every partner still parked is pulled back with them.
        internal static void OnRestored(Vector2s zone, List<ZDO> restored, int objectCount)
        {
            Forget(zone);
            var tags = new List<string>();
            for (int i = objectCount; i < restored.Count; i++) AddTag(tags, restored[i]);
            if (tags.Count == 0) return;
            if (!s_ready)
            {
                foreach (string tag in tags) s_waiting.Add(new KeyValuePair<string, Vector2s>(tag, zone));
                return;
            }
            foreach (string tag in tags) FollowTag(tag, zone);
        }

        internal static void Tick()
        {
            if (!ZoneModeRegistry.IsAuthority) return;
            Progress();
            if (s_ready && !s_raised && ZoneModeRegistry.Seeded)
            {
                s_raised = true;
                RaiseWayshrines();
            }
            OpenFollowed();
        }

        // ── Reading a park converted before any of this was written ─────────
        private static void Progress()
        {
            if (s_ready) return;
            if (!ZoneModePark.Converted || ZoneModePark.ParkedCount == 0)
            {
                s_ready = true;
                return;
            }
            if (s_unread == null)
            {
                s_unread = ZoneModePark.ParkedZoneList();
                FiresLogger.LogInfo($"[FiresZones] this world was converted before its portals and wayshrines were written down, "
                    + $"so its {s_unread.Count:N0} parked zone(s) are read once, {ObjectsReadPerTick:N0} object(s) a frame. Portals "
                    + "lead nowhere and shrines set to always show stay sunk until that finishes.");
            }
            int read = 0;
            while (read < ObjectsReadPerTick && s_unread.Count > 0)
            {
                int last = s_unread.Count - 1;
                var zone = s_unread[last];
                s_unread.RemoveAt(last);
                read += Math.Max(ReadZone(zone), 0);
            }
            if (s_unread.Count > 0) return;
            s_unread = null;
            Finish();
        }

        private static int ReadZone(Vector2s zone)
        {
            var tags = new List<string>();
            bool wayshrine = false;
            int read = ZoneModePark.TryReadZone(zone, (zdo, isPortal) =>
            {
                wayshrine |= IsAlwaysShown(zdo);
                if (isPortal) AddTag(tags, zdo);
            });
            if (tags.Count > 0 || wayshrine) Adopt(zone, tags.ToArray(), wayshrine);
            return read;
        }

        private static void Finish()
        {
            Write(s_folder);
            int shrines = 0;
            foreach (var pair in s_byZone) if (pair.Value.Wayshrine) shrines++;
            FiresLogger.LogInfo($"[FiresZones] parked landmarks: {s_byZone.Count} zone(s) hold {s_zonesByTag.Count} portal tag(s) "
                + $"and {shrines} wayshrine(s) set to always show.");
            s_raised = false;
            if (s_waiting.Count == 0) return;
            var waiting = new List<KeyValuePair<string, Vector2s>>(s_waiting);
            s_waiting.Clear();
            foreach (var pair in waiting) FollowTag(pair.Key, pair.Value);
        }

        // ── Opening what a landmark promises ────────────────────────────────
        // A shrine that shows on every map from the start needs its chunk from the start.
        private static void RaiseWayshrines()
        {
            var zones = new List<Vector2s>();
            foreach (var pair in s_byZone)
                if (pair.Value.Wayshrine && !ZoneModeRegistry.IsOpen(pair.Key) && ZoneModePark.TryGetParked(pair.Key, out _))
                    zones.Add(pair.Key);
            if (zones.Count == 0) return;
            var opened = ZoneModeOpening.Open(zones, ZoneOpenReason.Wayshrine, 0L, "a wayshrine there is set to always show");
            if (opened.Count > 0)
                FiresLogger.LogInfo($"[FiresZones] {opened.Count} chunk(s) opened because a wayshrine set to always show stands "
                    + $"there: {ZoneModeOpening.Describe(opened)}.");
        }

        private static void FollowTag(string tag, Vector2s from)
        {
            if (!s_zonesByTag.TryGetValue(tag, out var zones)) return;
            foreach (var zone in zones)
            {
                if (ZoneModeRegistry.IsOpen(zone) || !ZoneModePark.TryGetParked(zone, out _)) continue;
                bool queued = false;
                foreach (var follow in s_toFollow) queued |= follow.Zone.Equals(zone);
                if (queued) continue;
                s_toFollow.Add(new Follow { Zone = zone, From = from, Tag = tag });
            }
        }

        private static void OpenFollowed()
        {
            if (s_toFollow.Count == 0) return;
            var zones = new List<Vector2s>();
            var told = new List<string>();
            foreach (var follow in s_toFollow)
            {
                if (ZoneModeRegistry.IsOpen(follow.Zone)) continue;
                zones.Add(follow.Zone);
                told.Add($"'{follow.Tag}' from ({follow.From.x},{follow.From.y})");
            }
            s_toFollow.Clear();
            if (zones.Count == 0) return;
            string why = string.Join(", ", told.ToArray());
            var opened = ZoneModeOpening.Open(zones, ZoneOpenReason.PortalPartner, 0L, "portal partner: " + why);
            if (opened.Count > 0)
                FiresLogger.LogInfo($"[FiresZones] {opened.Count} chunk(s) opened because a portal found there leads to them: {why}.");
        }

        // ── What a zone holds ───────────────────────────────────────────────
        private static bool IsAlwaysShown(ZDO zdo) => zdo != null && zdo.GetBool(AutoDiscoverHash, false);

        private static void AddTag(List<string> tags, ZDO zdo)
        {
            string tag = zdo?.GetString(ZDOVars.s_tag);
            if (!string.IsNullOrEmpty(tag) && !tags.Contains(tag)) tags.Add(tag);
        }

        private static void Adopt(Vector2s zone, string[] tags, bool wayshrine)
        {
            s_byZone[zone] = new Held { PortalTags = tags, Wayshrine = wayshrine };
            foreach (string tag in tags)
            {
                if (!s_zonesByTag.TryGetValue(tag, out var zones)) s_zonesByTag[tag] = zones = new List<Vector2s>();
                if (!zones.Contains(zone)) zones.Add(zone);
            }
        }

        private static void Forget(Vector2s zone)
        {
            if (!s_byZone.TryGetValue(zone, out var held)) return;
            s_byZone.Remove(zone);
            foreach (string tag in held.PortalTags)
            {
                if (!s_zonesByTag.TryGetValue(tag, out var zones)) continue;
                zones.Remove(zone);
                if (zones.Count == 0) s_zonesByTag.Remove(tag);
            }
            if (s_ready && s_folder != null) Write(s_folder);
        }

        private static bool TryLoad()
        {
            string path = s_folder != null ? Path.Combine(s_folder, IndexFileName) : null;
            if (path == null || !File.Exists(path)) return false;
            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadUInt32() != IndexFileMagic || reader.ReadByte() != FileFormat)
                        throw new InvalidDataException("not a parked landmark file");
                    long uid = reader.ReadInt64();
                    if (uid != s_worldUid) throw new InvalidDataException($"it belongs to world uid {uid}");
                    int zones = reader.ReadInt32();
                    for (int i = 0; i < zones; i++)
                    {
                        var zone = new Vector2s(reader.ReadInt16(), reader.ReadInt16());
                        bool wayshrine = reader.ReadBoolean();
                        var tags = new string[reader.ReadInt32()];
                        for (int t = 0; t < tags.Length; t++) tags[t] = reader.ReadString();
                        Adopt(zone, tags, wayshrine);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                s_byZone.Clear();
                s_zonesByTag.Clear();
                FiresLogger.LogWarning($"[FiresZones] what the parked zones hold was unreadable ({ex.Message}); it is read again "
                    + "from the parked zones themselves.");
                return false;
            }
        }
    }
}
