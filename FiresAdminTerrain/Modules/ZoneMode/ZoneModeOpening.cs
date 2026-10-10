using System;
using System.Collections.Generic;
using FiresCore.Sync;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Zones
{
    // ── OPENING ZONES ───────────────────────────────────────────────────────
    // The server records the zones, saves the registry, republishes its own gate view and rebuilds
    // its own loaded terrain, then sends the full open set to every client. Each client swaps its
    // view, rebuilds the affected heightmaps and acknowledges. Only when every peer that was
    // connected has acknowledged (or AckTimeoutSeconds passes) does the server let vanilla populate
    // the zones, so their trees, rocks and location pieces always arrive over terrain that is
    // already land on every peer — a piece never spends its support checks over stale sea.
    internal static class ZoneModeOpening
    {
        private const string RpcZoneUpdate = "FiresAdminTerrain_ZoneUpdate";
        private const string RpcZoneAck = "FiresAdminTerrain_ZoneAck";
        private const string RpcOpenRequest = "FiresAdminTerrain_ZoneOpenRequest";
        private const string RpcReply = "FiresAdminTerrain_ZoneReply";
        private const string RpcSpawnerResult = "FiresAdminTerrain_SpawnerResult";
        private const float AckTimeoutSeconds = 12f;
        private const byte AdminOpenRequestKind = 1;
        private const byte SeedOpenRequestKind = 2;

        private sealed class PendingOpen
        {
            public int Version;
            public List<Vector2s> Zones;
            public HashSet<long> Awaiting;
            public float StartedAt;
            public float Deadline;
            public string Context;
        }

        private static readonly List<PendingOpen> s_pending = new List<PendingOpen>();
        private static readonly HashSet<Vector2s> s_awaitingZones = new HashSet<Vector2s>();
        private static ZRoutedRpc s_registeredOn;

        internal static bool IsAwaitingPeers(Vector2s zone) => s_awaitingZones.Contains(zone);
        internal static int PendingCount => s_pending.Count;

        internal static void EnsureRpcs()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ReferenceEquals(s_registeredOn, rpc)) return;
            try
            {
                rpc.Register<ZPackage>(RpcZoneUpdate, RPC_ZoneUpdate);
                rpc.Register<int>(RpcZoneAck, RPC_ZoneAck);
                rpc.Register<ZPackage>(RpcOpenRequest, RPC_OpenRequest);
                rpc.Register<string>(RpcReply, RPC_Reply);
                rpc.Register<ZPackage>(RpcSpawnerResult, RPC_SpawnerResult);
                ZoneModePark.RegisterParkDrop(rpc);
                s_registeredOn = rpc;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresZones] RPC register failed: {ex.Message}"); }
        }

        internal static void Reset()
        {
            s_pending.Clear();
            s_awaitingZones.Clear();
        }

        // ── Server ──────────────────────────────────────────────────────────
        internal static List<Vector2s> Open(IList<Vector2s> zones, ZoneOpenReason reason, long actorId, string context,
            IDictionary<Vector2s, Vector2s> sources = null, IDictionary<Vector2s, ZoneSystem.LocationInstance> locations = null,
            IDictionary<Vector2s, ZoneShift> shifts = null)
        {
            var land = WithParkedLand(zones, sources, shifts, out var landShifts);
            var opened = ZoneModeRegistry.AddOpenZones(zones, reason, actorId, land, landShifts);
            if (opened.Count == 0) return opened;
            // Builds made over these zones while they were closed come down now (only the ones tagged for them).
            try { ZoneModeOverClosed.TakeDown(opened); }
            catch (Exception ex) { FiresLogger.LogError($"[FiresZones] builds over {Describe(opened)} could not be taken down: {ex}"); }
            if (sources != null) ZoneModeSwaps.OnOpened(opened, sources, locations);

            var awaiting = new HashSet<long>();
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null && peer.IsReady() && peer.m_uid != 0L) awaiting.Add(peer.m_uid);
            // A host renders the world too, so it waits for its own terrain like any client.
            long localUid = ZNet.GetUID();
            bool hostShowsTerrain = Player.m_localPlayer != null;
            if (hostShowsTerrain) awaiting.Add(localUid);

            float now = Time.realtimeSinceStartup;
            var pending = new PendingOpen
            {
                Version = ZoneModeRegistry.Version,
                Zones = opened,
                Awaiting = awaiting,
                StartedAt = now,
                Deadline = now + AckTimeoutSeconds,
                Context = context,
            };
            s_pending.Add(pending);
            foreach (var zone in opened) s_awaitingZones.Add(zone);

            int remoteCount = awaiting.Count - (hostShowsTerrain ? 1 : 0);
            if (remoteCount > 0) Broadcast(opened);
            int version = pending.Version;
            ZoneModeTerrainRefresh.RebuildOpenedZones(opened,
                hostShowsTerrain ? (Action)(() => AcknowledgeLocally(version, localUid)) : null);
            FiresLogger.LogInfo($"[FiresZones] OPENED {opened.Count} zone(s) ({reason}, {context}): {Describe(opened)}. "
                + $"Registry v{version}; waiting for {remoteCount} client(s)" + (hostShowsTerrain ? " and this host's own terrain" : "")
                + " before vanilla populates them.");
            return opened;
        }

        // A parked zone that was a swapped zone returns on the land it stood on when it was taken back (its source and
        // shift, kept in its parked record), whatever opens it, unless a seed chose other land for it. Its location stays
        // as it was parked: the location hand-over of a fresh swap (ZoneModeSwaps.OnOpened) is not run for it.
        private static IDictionary<Vector2s, Vector2s> WithParkedLand(IList<Vector2s> zones, IDictionary<Vector2s, Vector2s> sources,
            IDictionary<Vector2s, ZoneShift> shifts, out IDictionary<Vector2s, ZoneShift> landShifts)
        {
            landShifts = shifts;
            Dictionary<Vector2s, Vector2s> land = null;
            foreach (var zone in zones)
            {
                if (sources != null && sources.ContainsKey(zone)) continue;
                if (!ZoneModePark.TryGetParked(zone, out var parked) || !parked.Swapped) continue;
                if (land == null)
                {
                    land = sources != null ? new Dictionary<Vector2s, Vector2s>(sources) : new Dictionary<Vector2s, Vector2s>();
                    landShifts = shifts != null ? new Dictionary<Vector2s, ZoneShift>(shifts) : new Dictionary<Vector2s, ZoneShift>();
                }
                land[zone] = parked.Source;
                landShifts[zone] = parked.Shift;
                FiresLogger.LogInfo($"[FiresZones] ({zone.x},{zone.y}) returns on the land it stood on: ({parked.Source.x},{parked.Source.y}), "
                    + $"lift {parked.Shift.Lift:0.0} m, slide ({parked.Shift.Dx:0.0},{parked.Shift.Dz:0.0}) m.");
            }
            return land ?? sources;
        }

        // Taking a chunk placement back takes its zone with it: the land the stamp laid down is already gone, and the
        // zone stops being open and stops being generated, so it is closed sea again and a different chunk can be laid
        // there. Nothing waits on peers here — a zone closing shows nothing that a piece could stand on.
        internal static void Close(IList<Vector2s> zones, string context)
        {
            var closed = ZoneModeRegistry.RemoveOpenZones(zones);
            if (closed.Count == 0) return;
            var generated = ZoneModeRegistry.GeneratedZones(ZoneSystem.instance);
            if (generated != null) foreach (var zone in closed) generated.Remove(zone);
            if (ZNet.instance != null && ZNet.instance.GetPeers().Count > 0) Broadcast(closed);
            ZoneModeTerrainRefresh.RebuildOpenedZones(closed, null);
            FiresLogger.LogInfo($"[FiresZones] CLOSED {closed.Count} zone(s) ({context}): {Describe(closed)}. "
                + $"Registry v{ZoneModeRegistry.Version}.");
        }

        private static void AcknowledgeLocally(int version, long localUid)
        {
            foreach (var pending in s_pending)
                if (pending.Version <= version) pending.Awaiting.Remove(localUid);
        }

        private static void Broadcast(List<Vector2s> opened)
        {
            ulong[] words = ZoneModeRegistry.CurrentWords();
            var bytes = new byte[words.Length * sizeof(ulong)];
            Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            var pkg = new ZPackage();
            pkg.Write(ZoneModeRegistry.Version);
            pkg.Write(bytes);
            pkg.Write(opened.Count);
            foreach (var zone in opened) pkg.Write(zone);
            ZoneModeRegistry.WriteSources(pkg, ZoneModeRegistry.CurrentSources());
            ZoneModeRegistry.WriteShifts(pkg, ZoneModeRegistry.CurrentShifts());
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcZoneUpdate, pkg);
        }

        // Server tick: drop peers that left, release zones every remaining peer has acknowledged.
        internal static void Tick()
        {
            if (s_pending.Count == 0 || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var connected = new HashSet<long> { ZNet.GetUID() };
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer != null) connected.Add(peer.m_uid);

            float now = Time.realtimeSinceStartup;
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                var pending = s_pending[i];
                pending.Awaiting.RemoveWhere(uid => !connected.Contains(uid));
                bool acknowledged = pending.Awaiting.Count == 0;
                if (!acknowledged && now < pending.Deadline) continue;

                s_pending.RemoveAt(i);
                foreach (var zone in pending.Zones) s_awaitingZones.Remove(zone);
                ZoneModePark.RestoreZones(pending.Zones, pending.Context);
                if (acknowledged)
                    FiresLogger.LogInfo($"[FiresZones] {Describe(pending.Zones)} released for population after "
                        + $"{(now - pending.StartedAt) * 1000f:0} ms — every client shows the new terrain.");
                else
                    FiresLogger.LogWarning($"[FiresZones] {Describe(pending.Zones)} released after the {AckTimeoutSeconds:0}s "
                        + $"timeout; {pending.Awaiting.Count} client(s) never confirmed their terrain ({NamesOf(pending.Awaiting)}). "
                        + "Their pieces there keep the 30 s grace from load; report this if anything collapsed.");
            }
        }

        private static void RPC_ZoneAck(long sender, int version)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            foreach (var pending in s_pending)
                if (pending.Version <= version && pending.Awaiting.Remove(sender))
                    FiresLogger.LogVerbose($"[FiresZones] {NameOf(sender)} confirmed terrain v{version} after "
                        + $"{(Time.realtimeSinceStartup - pending.StartedAt) * 1000f:0} ms.");
        }

        // A client (or the host's console) asks the server to open one zone.
        internal static void RequestAdminOpen(Terminal context, Vector2s zone, bool force)
        {
            if (ZNet.instance == null) return;
            if (ZNet.instance.IsServer())
            {
                context?.AddString(TryAdminOpen(zone, force, 0L, "console"));
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(AdminOpenRequestKind);
            pkg.Write(zone);
            pkg.Write(force);
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcOpenRequest, pkg);
            context?.AddString($"[Zones] asked the server to open ({zone.x},{zone.y})...");
        }

        // A player planted the chunk seed `seedPrefab` on `zone`; `choosingLand` is true while they wear the Eye of the
        // Beholder, the one way a seed raises other land over a parked zone.
        internal static void RequestSeedOpen(Vector2s zone, string seedPrefab, bool choosingLand)
        {
            if (ZNet.instance == null) return;
            if (ZNet.instance.IsServer())
            {
                bool opened = TrySeedOpen(zone, seedPrefab, choosingLand, ZNet.GetUID(), "the host", out string message);
                ZoneModeChunkSpawner.OnResult(opened, zone, message);
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(SeedOpenRequestKind);
            pkg.Write(zone);
            pkg.Write(false);
            pkg.Write(seedPrefab);
            pkg.Write(choosingLand);
            ZRoutedRpc.instance.InvokeRoutedRPC(RpcOpenRequest, pkg);
        }

        private static void RPC_OpenRequest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            byte kind = pkg.ReadByte();
            var zone = pkg.ReadVector2s();
            bool force = pkg.ReadBool();
            if (kind == SeedOpenRequestKind)
            {
                string seedPrefab = pkg.ReadString();
                bool choosingLand = pkg.ReadBool();
                bool opened = TrySeedOpen(zone, seedPrefab, choosingLand, sender, NameOf(sender), out string message);
                var result = new ZPackage();
                result.Write(opened);
                result.Write(zone);
                result.Write(message);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcSpawnerResult, result);
                return;
            }
            if (kind != AdminOpenRequestKind) return;
            string reply = AdminSyncing.IsAdmin(sender)
                ? TryAdminOpen(zone, force, sender, $"admin {NameOf(sender)}")
                : "[Zones] Permission denied — admin only.";
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcReply, reply);
        }

        private static bool TrySeedOpen(Vector2s zone, string seedPrefab, bool choosingLand, long actor, string who, out string message)
        {
            if (!ZoneModeRegistry.ModeActive || !ZoneModeRegistry.Seeded || !ZoneModeSeeds.TryGet(seedPrefab, out var seed))
            {
                message = "The land cannot answer yet.";
                return false;
            }
            message = ZoneModeChunkSpawner.Check(zone, Vector3.zero, false);
            if (message != null) return false;

            bool parked = ZoneModePark.TryGetParked(zone, out var park);
            if (parked && (!choosingLand || ZoneModePark.SeedMatches(seed, zone, park)))
                return TryOpenAsItWas(zone, seed, park, actor, who, out message);
            if (seed.RestoresOnly) return TryOpenAsWorldMadeIt(zone, seed, actor, who, out message);

            Vector2s source;
            ZoneShift centring = default;
            bool faithful;
            Dictionary<Vector2s, ZoneSystem.LocationInstance> locations = null;
            if (seed.IsLocation)
            {
                if (!ZoneModeSwaps.TryChooseLocationSource(zone, seed.Location, out source, out centring, out faithful, out var instance, out message))
                {
                    message = message ?? $"Nothing in this world matches the {seed.Name}: there is no such place to copy.";
                    return false;
                }
                if (!faithful) locations = new Dictionary<Vector2s, ZoneSystem.LocationInstance> { [zone] = instance };
            }
            else if (!ZoneModeSwaps.TryChooseSource(zone, seed.Tier.Biome, out source, out faithful))
            {
                message = "The land is still waking. Try again in a moment.";
                return false;
            }
            var sources = faithful ? null : new Dictionary<Vector2s, Vector2s> { [zone] = source };
            Dictionary<Vector2s, ZoneShift> shifts = null;
            string moved = string.Empty;
            if (!faithful)
            {
                float lift = ZoneModeLift.Choose(zone, source, centring, out string how);
                shifts = new Dictionary<Vector2s, ZoneShift> { [zone] = new ZoneShift(lift, centring.Dx, centring.Dz) };
                moved = (centring.Slides ? $" slid ({centring.Dx:0.0},{centring.Dz:0.0}) m to centre the location" : "")
                    + (lift == 0f ? $", at its own height ({how})" : $", {(lift > 0f ? "raised" : "lowered")} {Mathf.Abs(lift):0.0} m ({how})");
            }
            var opened = Open(new[] { zone }, ZoneOpenReason.Bought, actor,
                $"{seed.Name} used by {who}" + (faithful ? ", its own land" : $", the land of ({source.x},{source.y}){moved}"), sources, locations, shifts);
            if (opened.Count != 1)
            {
                message = "The land did not answer.";
                return false;
            }
            if (parked) ZoneModePark.Discard(zone, who);
            if (seed.IsLocation)
                message = $"The sea gives way — ({zone.x},{zone.y}) rises holding {seed.Title}.";
            else
                message = faithful
                    ? $"The sea gives way — ({zone.x},{zone.y}) rises as it was always meant to."
                    : $"The sea gives way — ({zone.x},{zone.y}) rises as {seed.Tier.Title} land.";
            if (parked) message += " What stood there is gone.";
            return true;
        }

        private static bool TryOpenAsItWas(Vector2s zone, ChunkSeed seed, ZoneModePark.ParkedZone park, long actor, string who, out string message)
        {
            var opened = Open(new[] { zone }, ZoneOpenReason.Bought, actor, $"{seed.Name} used by {who}, returning as it was");
            if (opened.Count != 1)
            {
                message = "The land did not answer.";
                return false;
            }
            message = $"The sea gives way — ({zone.x},{zone.y}) returns as it was"
                + (park.Pieces > 0 ? $", with the {park.Pieces} pieces built there." : ".");
            // Fire (10-03, popup b): the rule stays; the reason is said.
            if (seed.RestoresOnly) message += $" (The {seed.Name} restores only.)";
            else if (!ZoneModePark.SeedMatches(seed, zone, park)) message += $" (Parked land: wear the Eye of the Beholder to raise {seed.Land} here.)";
            return true;
        }

        // The plain seed on a zone nothing was parked in: its own generation, its own planned location, nothing swapped.
        private static bool TryOpenAsWorldMadeIt(Vector2s zone, ChunkSeed seed, long actor, string who, out string message)
        {
            var opened = Open(new[] { zone }, ZoneOpenReason.Bought, actor, $"{seed.Name} used by {who}, its own land");
            if (opened.Count != 1)
            {
                message = "The land did not answer.";
                return false;
            }
            message = $"The sea gives way — ({zone.x},{zone.y}) rises as the world made it.";
            return true;
        }

        private static void RPC_SpawnerResult(long sender, ZPackage pkg)
        {
            bool opened = pkg.ReadBool();
            var zone = pkg.ReadVector2s();
            string message = pkg.ReadString();
            ZoneModeChunkSpawner.OnResult(opened, zone, message);
        }

        private static string TryAdminOpen(Vector2s zone, bool force, long actor, string context)
        {
            if (!ZoneModeRegistry.ModeActive) return "[Zones] This is not a zone-mode world.";
            if (!ZoneModeRegistry.Seeded) return "[Zones] The world is not seeded yet.";
            if (!ZoneOpenSet.InRange(zone.x, zone.y)) return $"[Zones] ({zone.x},{zone.y}) is outside the world.";
            if (ZoneModeRegistry.IsOpen(zone)) return $"[Zones] ({zone.x},{zone.y}) is already open.";
            if (!force && !HasOpenEdgeNeighbour(zone))
                return $"[Zones] ({zone.x},{zone.y}) does not border an open zone. Add 'force' to open it anyway.";
            bool parked = ZoneModePark.TryGetParked(zone, out _);
            var opened = Open(new[] { zone }, ZoneOpenReason.Admin, actor, context);
            if (opened.Count != 1) return $"[Zones] ({zone.x},{zone.y}) could not be opened.";
            return $"[Zones] Opened ({zone.x},{zone.y})." + (parked ? " It returns as it was once every peer shows its land." : "");
        }

        internal static bool HasOpenEdgeNeighbour(Vector2s zone)
            => ZoneModeRegistry.IsOpen(new Vector2s(zone.x + 1, zone.y)) || ZoneModeRegistry.IsOpen(new Vector2s(zone.x - 1, zone.y))
               || ZoneModeRegistry.IsOpen(new Vector2s(zone.x, zone.y + 1)) || ZoneModeRegistry.IsOpen(new Vector2s(zone.x, zone.y - 1));

        // ── Client ──────────────────────────────────────────────────────────
        private static void RPC_ZoneUpdate(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return;
            int version = pkg.ReadInt();
            byte[] bytes = pkg.ReadByteArray();
            int count = pkg.ReadInt();
            var opened = new List<Vector2s>(count);
            for (int i = 0; i < count; i++) opened.Add(pkg.ReadVector2s());
            List<KeyValuePair<Vector2s, Vector2s>> sources;
            List<KeyValuePair<Vector2s, ZoneShift>> shifts;
            try
            {
                sources = ZoneModeRegistry.ReadSources(pkg);
                shifts = ZoneModeRegistry.ReadShifts(pkg);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"[FiresZones] zone update v{version} has unreadable zone sources ({ex.Message}) — ignored.");
                return;
            }

            if (bytes == null || bytes.Length != ZoneOpenSet.WordCount * sizeof(ulong))
            {
                FiresLogger.LogError($"[FiresZones] zone update v{version} carried {bytes?.Length ?? 0} bytes of open set — ignored.");
                return;
            }
            var words = new ulong[ZoneOpenSet.WordCount];
            Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
            if (!ZoneModeRegistry.ApplyClientUpdate(words, version, sources, shifts)) return;

            FiresLogger.LogInfo($"[FiresZones] zone update v{version}: {Describe(opened)} opened — rebuilding terrain.");
            ZoneModeTerrainRefresh.RebuildOpenedZones(opened,
                () => ZRoutedRpc.instance?.InvokeRoutedRPC(RpcZoneAck, version));
        }

        private static void RPC_Reply(long sender, string message)
        {
            try { Console.instance?.AddString(message); } catch { }
            FiresLogger.LogInfo(message);
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        internal static string Describe(IList<Vector2s> zones)
        {
            const int Shown = 6;
            var parts = new List<string>();
            for (int i = 0; i < zones.Count && i < Shown; i++) parts.Add($"({zones[i].x},{zones[i].y})");
            return string.Join(" ", parts) + (zones.Count > Shown ? $" +{zones.Count - Shown} more" : "");
        }

        private static string NameOf(long uid)
        {
            var peer = ZNet.instance?.GetPeer(uid);
            return peer != null && !string.IsNullOrEmpty(peer.m_playerName) ? peer.m_playerName : uid.ToString();
        }

        private static string NamesOf(IEnumerable<long> uids)
        {
            var names = new List<string>();
            foreach (long uid in uids) names.Add(NameOf(uid));
            return string.Join(", ", names);
        }
    }
}
