using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── CLOSING AN OPEN ZONE AT RUNTIME (FAT 0.2.718; Fire 10-05) ───────────
    // "set up the chaos Hammer to be able to hide chunks on a non-chunk by chunk world as long as it's converted to voxel
    // wrap/full voxel ... forcing players to have to unlock it somehow". The reverse of opening: a zone is set aside into
    // the PARKED slot exactly as a converted world's first load parks it (never deleted; reopening is the ordinary restore),
    // and it becomes closed, locked sea that only a Chunk Generator seed, an admin or a quest can open again.
    //
    // With players connected it cannot happen in one call. DestroyZDO would put every object's id in each client's
    // m_deadZDOs, and they would refuse the zone's objects when it reopens; dropping them on the server only would leave
    // them on clients, and a client that owns one could send it back. So:
    //   1. TryParkOpenZones checks and refuses, then the server takes ownership of every object in the zones;
    //   2. CloseHandoverSeconds later (clients have stopped writing them), CloseTick writes each zone to the Parked slot and
    //      commits the index, tells every client to drop those objects locally (no dead mark), drops them on the server,
    //      takes the zones out of the generated set, closes and locks them, and reports through `done`.
    // A refusal never calls `done`.
    internal static partial class ZoneModePark
    {
        private const float CloseHandoverSeconds = 2.5f;
        internal const float ClosePlayerClearance = 8f;   // internal since 0.2.753: the Chaos Hammer's zone highlight predicts the refusal
        private const float ZoneHalf = 32f;
        private const string RpcParkDrop = "FiresZones_ParkDrop";
        private static readonly int TombstoneHash = "Player_tombstone".GetStableHashCode();

        private sealed class PendingClose
        {
            public List<Vector2s> Zones;
            public long Actor;
            public Action<List<Vector2s>, string> Done;
            public float At;
        }

        private static readonly List<PendingClose> s_closing = new List<PendingClose>();
        private static readonly HashSet<Vector2s> s_closingZones = new HashSet<Vector2s>();

        // Why this world cannot close zones now, for the Chaos Hammer to show before or after a swing.
        // The loaded world on the server, zone mode or not (make-closeable needs it before the registry exists).
        internal static World ServerWorld => s_server ? s_world : null;

        internal static bool CanClose(out string why)
        {
            why = null;
            if (!s_server || s_world == null || ZDOMan.instance == null) { why = "Only the server can close a chunk."; return false; }
            if (!ZoneModeRegistry.ModeActive)
            {
                why = $"'{s_world.m_name}' is a full world that is not closeable yet: its owner runs fires_zones_make_closeable on its "
                    + "server once, then restarts it.";
                return false;
            }
            var profile = Voxel.FiresWorldProfile.Current;
            if (profile == null || profile.Terrain == Voxel.ProfileTerrain.Heightmap)
            {
                why = "Closing chunks needs a voxel-wrap or full-voxel world.";
                return false;
            }
            return true;
        }

        internal static bool TryParkOpenZone(Vector2s zone, long actorId, Action<List<Vector2s>, string> done, out string report)
            => TryParkOpenZones(new[] { zone }, actorId, done, out report);

        // Checks, takes ownership and queues the close: true = accepted (`done` fires later with what was parked), false =
        // refused (`report` says why; `done` never fires).
        internal static bool TryParkOpenZones(IList<Vector2s> zones, long actorId, Action<List<Vector2s>, string> done, out string report)
        {
            if (!CanClose(out report)) return false;
            var accepted = new List<Vector2s>();
            var refused = new List<string>();
            var zdoMan = ZDOMan.instance;
            foreach (var zone in zones)
            {
                if (accepted.Contains(zone)) continue;
                string why = null;
                if (!ZoneOpenSet.InRange(zone.x, zone.y)) why = "is beyond the zone grid";
                else if (!ZoneModeRegistry.IsOpen(zone)) why = "is not open";
                else if (s_closingZones.Contains(zone)) why = "is closing already";
                else if (PlayerNear(zone, out string who)) why = $"{who} is within {ClosePlayerClearance:0} m";
                else if (HoldsTombstone(zdoMan, zone)) why = "a tombstone lies in it";
                if (why != null) refused.Add($"({zone.x},{zone.y}) {why}");
                else accepted.Add(zone);
            }
            if (accepted.Count == 0)
            {
                report = "Nothing closed: " + string.Join("; ", refused) + ".";
                return false;
            }

            long self = ZDOMan.GetSessionID();
            int owned = 0;
            foreach (var zone in accepted)
            {
                var objects = new List<ZDO>();
                var portals = new List<ZDO>();
                CollectZone(zdoMan, zone, objects, portals);
                objects.AddRange(portals);
                foreach (var zdo in objects)
                    if (!zdo.IsOwner()) { zdo.SetOwner(self); owned++; }
                s_closingZones.Add(zone);
            }
            s_closing.Add(new PendingClose { Zones = accepted, Actor = actorId, Done = done, At = Time.realtimeSinceStartup + CloseHandoverSeconds });
            report = $"Closing {Describe(accepted)}…" + (refused.Count > 0 ? $" ({refused.Count} refused: {string.Join("; ", refused)})" : "");
            FiresLogger.LogInfo($"[FiresZones] closing {Describe(accepted)} for actor {actorId}: took ownership of {owned} object(s); "
                + $"set aside in {CloseHandoverSeconds:0.#} s." + (refused.Count > 0 ? $" Refused: {string.Join("; ", refused)}." : ""));
            return true;
        }

        // From ZoneModeDiagnostics' tick (server).
        internal static void CloseTick()
        {
            if (s_closing.Count == 0 || !s_server || ZDOMan.instance == null) return;
            float now = Time.realtimeSinceStartup;
            for (int i = s_closing.Count - 1; i >= 0; i--)
            {
                var pending = s_closing[i];
                if (now < pending.At) continue;
                s_closing.RemoveAt(i);
                foreach (var zone in pending.Zones) s_closingZones.Remove(zone);
                List<Vector2s> parked;
                string report;
                try { parked = ParkNow(pending.Zones, pending.Actor, out report); }
                catch (Exception ex)
                {
                    parked = new List<Vector2s>();
                    report = $"Closing {Describe(pending.Zones)} failed ({ex.GetType().Name}: {ex.Message}); nothing was removed.";
                    FiresLogger.LogError($"[FiresZones] {report} {ex}");
                }
                try { pending.Done?.Invoke(parked, report); }
                catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] a close's report could not be delivered: {ex.Message}"); }
            }
        }

        private static List<Vector2s> ParkNow(List<Vector2s> zones, long actor, out string report)
        {
            var zdoMan = ZDOMan.instance;
            var zoneSystem = ZoneSystem.instance;
            var parked = new List<Vector2s>();
            var collected = new Dictionary<Vector2s, List<ZDO>>();
            var entries = new Dictionary<Vector2s, ParkedZone>();
            int pieces = 0, portalCount = 0, objectCount = 0;
            // Re-checked: a player may have walked in during the hand-over.
            var still = new List<Vector2s>();
            var late = new List<string>();
            foreach (var zone in zones)
            {
                if (!ZoneModeRegistry.IsOpen(zone)) { late.Add($"({zone.x},{zone.y}) is no longer open"); continue; }
                if (PlayerNear(zone, out string who)) { late.Add($"({zone.x},{zone.y}) {who} walked within {ClosePlayerClearance:0} m"); continue; }
                still.Add(zone);
            }
            if (still.Count == 0)
            {
                report = "Nothing closed: " + string.Join("; ", late) + ".";
                FiresLogger.LogInfo($"[FiresZones] {report}");
                return parked;
            }

            EnsureStore();
            var serializer = new ZoneSerializer();
            ZDOExtraData.PrepareSave();
            try
            {
                foreach (var zone in still)
                {
                    var objects = new List<ZDO>();
                    var portals = new List<ZDO>();
                    CollectZone(zdoMan, zone, objects, portals);
                    var entry = new ParkedZone
                    {
                        Pieces = CountBuiltPieces(objects) + CountBuiltPieces(portals),
                        LocationHash = zoneSystem != null ? LocationHashAt(zoneSystem, zone) : 0,
                    };
                    if (ZoneModeRegistry.TryGetSource(zone, out var source))
                    {
                        entry.Swapped = true;
                        entry.Source = source;
                        entry.Shift = ZoneModeRegistry.ShiftOf(zone);
                    }
                    byte[] bytes = serializer.Serialize(entry, objects, portals);
                    s_store.Write(zone, ParkSlot.Parked, entry, bytes);
                    if (s_store.Has(zone, ParkSlot.Restored)) s_store.Remove(zone, ParkSlot.Restored);
                    ZoneModeLandmarks.Record(zone, objects, portals);
                    entries[zone] = entry;
                    pieces += entry.Pieces;
                    portalCount += portals.Count;
                    objects.AddRange(portals);
                    objectCount += objects.Count;
                    collected[zone] = objects;
                }
            }
            finally { ZDOExtraData.ClearSave(); }
            foreach (var pair in entries) s_index[pair.Key] = pair.Value;
            IndexGeneration++;
            s_store.Commit(s_index);

            // Every client drops the same objects locally, with no dead mark, so they come back when the zone reopens.
            var pkg = new ZPackage();
            pkg.Write(collected.Count);
            foreach (var pair in collected)
            {
                pkg.Write((int)pair.Key.x);
                pkg.Write((int)pair.Key.y);
                pkg.Write(pair.Value.Count);
                foreach (var zdo in pair.Value) pkg.Write(zdo.m_uid);
            }
            ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcParkDrop, pkg);

            var generated = zoneSystem != null ? GeneratedZonesRef(zoneSystem) : null;
            foreach (var pair in collected)
            {
                RemoveFromLiveWorld(zdoMan, pair.Key, pair.Value);
                generated?.Remove(pair.Key);
                parked.Add(pair.Key);
            }
            ResetChunkStore(zdoMan);
            ZoneModeOpening.Close(parked, $"closed by actor {actor}");
            foreach (var zone in parked) ZoneModeChunkRemoval.DropZoneVoxelsSoon(zone);   // 0.2.753: a listen host's own view
            ZoneModeRegistry.Lock(parked);

            report = (parked.Count == 1
                    ? $"Closed ({parked[0].x},{parked[0].y}): "
                    : $"Closed {parked.Count} chunk(s) {Describe(parked)}: ")
                + $"{pieces} piece(s), {portalCount} portal(s) set aside. They come back when it opens."
                + (late.Count > 0 ? $" Not closed: {string.Join("; ", late)}." : "");
            FiresLogger.LogInfo($"[FiresZones] CLOSED and set aside {Describe(parked)} for actor {actor}: {objectCount} object(s), {pieces} "
                + $"built piece(s), {portalCount} portal(s), in {s_store.Describe()} (Parked slot; it returns as it was when opened). "
                + "Locked: only a Chunk Generator seed, an admin or a quest opens it.");
            return parked;
        }

        // Client side of the drop (the server already dropped its own copies).
        internal static void RPC_ParkDrop(long sender, ZPackage pkg)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || (ZNet.instance != null && ZNet.instance.IsServer())) return;
            int zones = pkg.ReadInt();
            int dropped = 0;
            for (int z = 0; z < zones; z++)
            {
                var zone = new Vector2s(pkg.ReadInt(), pkg.ReadInt());
                int count = pkg.ReadInt();
                var list = new List<ZDO>(count);
                for (int i = 0; i < count; i++)
                {
                    var zdo = zdoMan.GetZDO(pkg.ReadZDOID());
                    if (zdo != null) list.Add(zdo);
                }
                RemoveFromLiveWorld(zdoMan, zone, list);
                dropped += list.Count;
                ZoneModeChunkRemoval.DropZoneVoxelsSoon(zone);   // 0.2.753: its terrain goes too, so it shows closed sea
            }
            FiresLogger.LogDebug($"[FiresZones] dropped {dropped} object(s) of {zones} closed zone(s) (the server set them aside).");
        }

        internal static void RegisterParkDrop(ZRoutedRpc rpc) => rpc.Register<ZPackage>(RpcParkDrop, RPC_ParkDrop);

        private static bool PlayerNear(Vector2s zone, out string who)
        {
            who = null;
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            bool Near(Vector3 p) =>
                Mathf.Max(0f, Mathf.Abs(p.x - centre.x) - ZoneHalf) <= ClosePlayerClearance
                && Mathf.Max(0f, Mathf.Abs(p.z - centre.z) - ZoneHalf) <= ClosePlayerClearance;
            var local = Player.m_localPlayer;
            if (local != null && Near(local.transform.position)) { who = local.GetPlayerName(); return true; }
            if (ZNet.instance != null)
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null && peer.IsReady() && Near(peer.m_refPos)) { who = peer.m_playerName; return true; }
            return false;
        }

        private static bool HoldsTombstone(ZDOMan zdoMan, Vector2s zone)
        {
            var objects = new List<ZDO>();
            var portals = new List<ZDO>();
            CollectZone(zdoMan, zone, objects, portals);
            foreach (var zdo in objects) if (zdo.GetPrefab() == TombstoneHash) return true;
            return false;
        }

        private static string Describe(List<Vector2s> zones)
        {
            var parts = new List<string>();
            foreach (var z in zones) parts.Add($"({z.x},{z.y})");
            return string.Join(" ", parts);
        }
    }
}
