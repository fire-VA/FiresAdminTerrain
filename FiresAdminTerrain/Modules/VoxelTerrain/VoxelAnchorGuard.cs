using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // ── Server guard on voxel edit records (0.2.1454, [ghost]; lead 11-17 Voxheim diff row P7, policy with [worldgen]) ─────────
    // A voxel edit travels as its chunk's VAVoxel anchor ZDO, written by the editing CLIENT (VoxelWorld.SaveChunk) and spread by
    // vanilla ZDO sync. Until now nothing on the server checked who wrote one: the admin gate on the V-tools is client side
    // (VoxelToolPieces menu, VoxelBrushDrag refusal), so a modified client could stamp any terrain anywhere. The tile brush and
    // water RPCs are already admin-checked on the server (TileBrushRPC, WaterOperationRPC); this closes the voxel path the same way.
    //
    // Non-admins DO write anchors legitimately, so they cannot simply be refused: the pickaxe dig (VoxelOnlyPatches, r 1 carve),
    // companions' picks (same path on their owner client), the hoe / cultivator level and raise in caves, on ground over caves and on
    // sky islands (FiresLayerTerrainEdit / FiresSkyIslandTerrainEdit: fills AND carves in 1.5-2 m cylinder stamps, up to 16 m of
    // height), layer paint on cave floors and islands (VoxelLayerPaint, incl. snow trails). So a non-admin write is MERGED sample by
    // sample against the server's copy: changes that fit hand-tool sized boxes outside other players' wards are kept, the rest put
    // back. Admins and the server itself: untouched. Only when this runs: on the server, inside ZDOMan.RPC_ZDOData (the one place a
    // received ZDO's sender is known), for VAVoxel ZDOs only.
    //
    // Rejected samples are put back by writing the merged record with the server as owner, so its data revision rises and every
    // peer (the sender too) re-applies the true state. The mop tail (water removal) is admin-only and always kept. One Warning per
    // rejected write (at most 3 per sender a minute, then a 60 s rollup), a Debug count line a minute.
    internal static class VoxelAnchorGuard
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> BoxWidth, BoxHeight;
        internal static ConfigEntry<int> MaxBoxes, MaxSamplesPerWrite, MaxSamplesPer10s;

        internal static void Bind(ConfigFile cfg)
        {
            const string S = "Voxel Terrain";
            Enabled = cfg.Bind(S, "Server rejects non-admin voxel writes", true,
                "On a server: a non-admin player's voxel terrain change is kept only where it is hand-tool sized (pickaxe, hoe, "
                + "cultivator, paint) and outside wards that player is not permitted on; anything else they send is put back. Admins are never "
                + "checked. Only the server's value matters.");
            BoxWidth = cfg.Bind(S, "Non-admin edit box width", 14f, new ConfigDescription(
                "Largest width (metres, east-west and north-south) of one group of terrain changes a non-admin may send at once. A hoe "
                + "level is about 10 m, a pickaxe swing 3 m.", new AcceptableValueRange<float>(4f, 64f)));
            BoxHeight = cfg.Bind(S, "Non-admin edit box height", 24f, new ConfigDescription(
                "Largest height (metres) of one such group. A hoe level on a sky island can move the surface up to 16 m.",
                new AcceptableValueRange<float>(4f, 64f)));
            MaxBoxes = cfg.Bind(S, "Non-admin edit groups per save", 8, new ConfigDescription(
                "How many separate groups one non-admin save of a chunk may carry (several swings land in one save).",
                new AcceptableValueRange<int>(1, 64)));
            MaxSamplesPerWrite = cfg.Bind(S, "Non-admin samples per save", 12000, new ConfigDescription(
                "Most terrain samples (1 m points) one non-admin save may change.", new AcceptableValueRange<int>(500, 200000)));
            MaxSamplesPer10s = cfg.Bind(S, "Non-admin samples per 10 s", 60000, new ConfigDescription(
                "Most terrain samples one non-admin player may change in any 10 seconds, all chunks together.",
                new AcceptableValueRange<int>(2000, 2000000)));
        }

        // The peer whose ZDO package is being read (main thread; set and cleared around ZDOMan.RPC_ZDOData).
        private static ZNetPeer s_sender;
        private static readonly System.Reflection.MethodInfo s_findPeer = AccessTools.Method(typeof(ZDOMan), "FindPeer", new[] { typeof(ZRpc) });
        private static readonly System.Reflection.FieldInfo s_peerField = AccessTools.Field(AccessTools.Inner(typeof(ZDOMan), "ZDOPeer"), "m_peer");

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        private static class ZDOMan_RPC_ZDOData
        {
            private static void Prefix(ZDOMan __instance, ZRpc rpc)
            {
                s_sender = null;
                if (Enabled == null || !Enabled.Value || ZNet.instance == null || !ZNet.instance.IsServer() || s_findPeer == null || s_peerField == null) return;
                try { var zp = s_findPeer.Invoke(__instance, new object[] { rpc }); s_sender = zp != null ? s_peerField.GetValue(zp) as ZNetPeer : null; }
                catch { s_sender = null; }
            }

            private static System.Exception Finalizer(System.Exception __exception) { s_sender = null; return __exception; }
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
        private static class ZDO_Deserialize
        {
            private static void Prefix(ZDO __instance, out byte[] __state)
            {
                __state = null;
                if (s_sender == null || __instance == null || __instance.GetPrefab() != VoxelAnchorPrefab.PrefabHash) return;
                __state = __instance.GetByteArray(VoxelAnchorPrefab.ZDO_EDITS) ?? NoBytes;   // NoBytes = "had no record" (a new anchor)
            }

            private static void Postfix(ZDO __instance, byte[] __state)
            {
                if (__state == null || s_sender == null) return;
                try { Check(__instance, s_sender, ReferenceEquals(__state, NoBytes) ? null : __state); }
                catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] anchor guard failed on a write from {s_sender.m_playerName}: {ex.Message} (the write was left as sent)"); }
            }
        }

        private static readonly byte[] NoBytes = new byte[0];
        private const int S = VoxelChunk.SampleCount, N = VoxelChunk.SAMPLES;

        private static void Check(ZDO zdo, ZNetPeer peer, byte[] beforePacked)
        {
            byte[] afterPacked = zdo.GetByteArray(VoxelAnchorPrefab.ZDO_EDITS);
            if (afterPacked == null || (beforePacked != null && SameBytes(beforePacked, afterPacked))) return;   // no edit change in this update
            // 0.2.1470: a dedicated server holds no anchor instances (VoxelAnchorPrefab.FindZdo's note), so no chunk import ever bumps its
            // VoxelEditRevision; a changed anchor record arriving here does (FiresGodComplex re-reads the zone). Admin writes included.
            int nkx = zdo.GetInt(VoxelAnchorPrefab.ZDO_KX, int.MinValue);
            if (nkx != int.MinValue) VoxelEditRevision.Note(new Vector3Int(nkx, zdo.GetInt(VoxelAnchorPrefab.ZDO_KY), zdo.GetInt(VoxelAnchorPrefab.ZDO_KZ)));
            if (global::VerdantsAscent.Terrain.Lifecycle.AdminCheck.IsAdmin(peer.m_uid)) { s_adminWrites++; NoteCounts(Time.realtimeSinceStartup); return; }

            int kx = zdo.GetInt(VoxelAnchorPrefab.ZDO_KX, int.MinValue);
            var after = FatInflate.Gzip(afterPacked, "voxel edits ZDO (guard)");
            var before = beforePacked != null ? FatInflate.Gzip(beforePacked, "voxel edits ZDO (guard)") : null;
            if (before == null || before.Length < S * 2) before = EmptyRecord();
            string who = $"{peer.m_playerName} ({peer.m_uid})";
            if (kx == int.MinValue || after == null || after.Length < S * 2)
            { Reject(zdo, before, who, "unreadable record", null, 0); return; }
            var key = new Vector3Int(kx, zdo.GetInt(VoxelAnchorPrefab.ZDO_KY), zdo.GetInt(VoxelAnchorPrefab.ZDO_KZ));

            long playerId = 0;
            var charZdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(peer.m_characterID) : null;
            if (charZdo != null) playerId = charZdo.GetLong(ZDOVars.s_playerID, 0L);
            if (playerId == 0) { Reject(zdo, before, who, "the sender's player could not be resolved (fail closed)", key, 0); return; }

            ReadTails(before, out int bMopAt, out int bMopLen, out byte[] bPaint);
            ReadTails(after, out _, out _, out byte[] aPaint);

            // the samples this write changes
            s_changed.Clear();
            for (int i = 0; i < S; i++)
                if (before[i] != after[i] || before[S + i] != after[S + i] || PaintOf(bPaint, i) != PaintOf(aPaint, i)) s_changed.Add(i);
            bool mopChanged = !SameMop(before, after);
            if (s_changed.Count == 0)
            {
                if (mopChanged) Reject(zdo, before, who, "water removal is admin-only", key, 0);
                return;
            }

            // per-sender rate
            float now = Time.realtimeSinceStartup;
            var rate = Rate(peer.m_uid, now);
            if (s_changed.Count > MaxSamplesPerWrite.Value) { Reject(zdo, before, who, $"{s_changed.Count} samples in one save (limit {MaxSamplesPerWrite.Value})", key, s_changed.Count); return; }
            if (rate.Samples + s_changed.Count > MaxSamplesPer10s.Value) { Reject(zdo, before, who, $"over {MaxSamplesPer10s.Value} samples in 10 s", key, s_changed.Count); return; }

            // group into hand-tool boxes
            var origin = new Vector3(key.x * 32f, key.y * 32f, key.z * 32f);
            s_boxes.Clear();
            s_boxOf.Clear();
            float w = BoxWidth.Value, h = BoxHeight.Value;
            foreach (int i in s_changed)
            {
                Vector3 p = origin + Local(i);
                int b = -1;
                for (int k = 0; k < s_boxes.Count; k++)
                {
                    var g = s_boxes[k].Bounds;
                    g.Encapsulate(p);
                    if (g.size.x <= w && g.size.z <= w && g.size.y <= h) { b = k; var e = s_boxes[k]; e.Bounds = g; s_boxes[k] = e; break; }
                }
                if (b < 0)
                {
                    if (s_boxes.Count >= MaxBoxes.Value) { Reject(zdo, before, who, $"changes spread over more than {MaxBoxes.Value} groups of {w:0}x{h:0}x{w:0} m", key, s_changed.Count); return; }
                    s_boxes.Add(new Box { Bounds = new Bounds(p, Vector3.zero) });
                    b = s_boxes.Count - 1;
                }
                s_boxOf.Add(b);
            }
            // wards the sender is not permitted on
            int deniedBoxes = 0;
            for (int k = 0; k < s_boxes.Count; k++)
            {
                var e = s_boxes[k];
                e.Denied = InForeignWard(e.Bounds, playerId);
                if (e.Denied) deniedBoxes++;
                s_boxes[k] = e;
            }

            rate.Samples += s_changed.Count;
            if (deniedBoxes == 0 && !mopChanged) { s_accepted++; NoteCounts(now); return; }   // the whole write stands

            // merge: the before record, with the samples of allowed boxes taken from the write; the mop tail always kept
            var lo = new byte[S]; var hi = new byte[S];
            System.Buffer.BlockCopy(before, 0, lo, 0, S);
            System.Buffer.BlockCopy(before, S, hi, 0, S);
            byte[] paint = bPaint != null ? (byte[])bPaint.Clone() : null;
            int kept = 0, dropped = 0;
            for (int n = 0; n < s_changed.Count; n++)
            {
                int i = s_changed[n];
                bool shape = before[i] != after[i] || before[S + i] != after[S + i];
                // paint alone is never put back for a ward: vanilla's snow / creature trails paint inside any ward (cosmetic)
                if (shape && s_boxes[s_boxOf[n]].Denied) { dropped++; continue; }
                lo[i] = after[i]; hi[i] = after[S + i];
                byte ap = PaintOf(aPaint, i);
                if (ap != PaintOf(paint, i)) { if (paint == null) paint = new byte[S]; paint[i] = ap; }
                kept++;
            }
            if (dropped == 0 && !mopChanged) { s_accepted++; NoteCounts(now); return; }   // only paint in a ward: the write stands as sent
            var merged = Build(lo, hi, before, bMopAt, bMopLen, paint);
            string why = deniedBoxes > 0 ? $"{deniedBoxes} of {s_boxes.Count} group(s) inside a ward this player is not permitted on" : "";
            if (mopChanged) why += (why.Length > 0 ? "; " : "") + "water removal is admin-only";
            Write(zdo, merged);
            s_rejected++; s_droppedSamples += dropped;
            Warn(peer.m_uid, $"[FiresVoxel] anchor guard: put back part of a non-admin terrain write from {who}, chunk {key}: {why}; {kept} sample(s) kept, {dropped} put back");
            NoteCounts(now);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────────
        private struct Box { public Bounds Bounds; public bool Denied; }
        private static readonly List<int> s_changed = new List<int>();
        private static readonly List<Box> s_boxes = new List<Box>();
        private static readonly List<int> s_boxOf = new List<int>();

        private static Vector3 Local(int i)
        {
            int x = i % N, r = i / N, y = r % N, z = r / N;
            return new Vector3(x - 1, y - 1, z - 1);   // VoxelChunk.Idx: (x+1) + N*((y+1) + N*(z+1))
        }

        private static byte[] EmptyRecord()
        {
            var b = new byte[S * 2];
            for (int i = 0; i < S; i++) { b[i] = unchecked((byte)(sbyte)-127); b[S + i] = 127; }   // VoxelChunk EditLoNone / EditHiNone
            return b;
        }

        // The blob layout lives in VoxelEditBlob since 0.2.1455 (shared with the undo merge, Voxheim diff P6).
        private static void ReadTails(byte[] raw, out int mopAt, out int mopLen, out byte[] paint) => VoxelEditBlob.ReadTails(raw, out mopAt, out mopLen, out paint);
        private static byte PaintOf(byte[] paint, int i) => VoxelEditBlob.PaintOf(paint, i);
        private static bool SameMop(byte[] a, byte[] b) => VoxelEditBlob.SameMop(a, b);
        private static byte[] Build(byte[] lo, byte[] hi, byte[] mopSource, int mopAt, int mopLen, byte[] paint) => VoxelEditBlob.Build(lo, hi, mopSource, mopAt, mopLen, paint);

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // A ward (vanilla PrivateArea, live on the server near its players) that is on, reaches the box, and neither belongs to nor
        // permits this player. Vanilla only checks wards against the local player (PrivateArea.HaveLocalAccess), so the same test is
        // made here by player id: creator or permitted list.
        private static readonly System.Reflection.FieldInfo s_allAreas = AccessTools.Field(typeof(PrivateArea), "m_allAreas");
        private static readonly System.Reflection.MethodInfo s_isPermitted = AccessTools.Method(typeof(PrivateArea), "IsPermitted", new[] { typeof(long) });
        private static readonly System.Reflection.FieldInfo s_piece = AccessTools.Field(typeof(PrivateArea), "m_piece");
        private static readonly System.Reflection.MethodInfo s_isEnabled = AccessTools.Method(typeof(PrivateArea), "IsEnabled");   // private in vanilla

        private static bool WardOn(PrivateArea area)
        {
            if (s_isEnabled == null) return true;   // method not found = treat as on (fail closed)
            try { return (bool)s_isEnabled.Invoke(area, null); }
            catch { return true; }   // unreadable = treat as on (fail closed)
        }

        private static bool InForeignWard(Bounds box, long playerId)
        {
            if (!(s_allAreas?.GetValue(null) is List<PrivateArea> areas) || areas.Count == 0) return false;
            Vector3 c = box.center;
            float reach = new Vector2(box.extents.x, box.extents.z).magnitude;
            foreach (var area in areas)
            {
                if (area == null || !WardOn(area)) continue;
                if (Utils.DistanceXZ(area.transform.position, c) >= area.m_radius + reach) continue;
                var piece = s_piece?.GetValue(area) as Piece;
                if (piece != null && piece.GetCreator() == playerId) continue;
                bool permitted = false;
                try { permitted = s_isPermitted != null && (bool)s_isPermitted.Invoke(area, new object[] { playerId }); } catch { }
                if (!permitted) return true;
            }
            return false;
        }

        private static void Reject(ZDO zdo, byte[] before, string who, string why, Vector3Int? key, int samples)
        {
            Write(zdo, before);
            s_rejected++; s_droppedSamples += samples;
            Warn(s_sender != null ? s_sender.m_uid : 0L, $"[FiresVoxel] anchor guard: put back a non-admin terrain write from {who}{(key.HasValue ? $", chunk {key.Value}" : "")}: {why}");
            NoteCounts(Time.realtimeSinceStartup);
        }

        // The server takes the anchor and writes the true record: its revision rises, so every peer, the sender too, re-applies it.
        private static void Write(ZDO zdo, byte[] raw)
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
            zdo.Set(VoxelAnchorPrefab.ZDO_EDITS, Utils.Compress(raw));
        }

        // ── rate, logging ────────────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class RateState { public float Since; public int Samples; public float WarnSince; public int Warns, Quiet; }
        private static readonly Dictionary<long, RateState> s_rates = new Dictionary<long, RateState>();

        private static RateState Rate(long uid, float now)
        {
            if (!s_rates.TryGetValue(uid, out var r)) { r = new RateState { Since = now, WarnSince = now }; s_rates[uid] = r; if (s_rates.Count > 512) s_rates.Clear(); }
            if (now - r.Since > 10f) { r.Since = now; r.Samples = 0; }
            return r;
        }

        private static void Warn(long uid, string line)
        {
            var r = Rate(uid, Time.realtimeSinceStartup);
            float now = Time.realtimeSinceStartup;
            if (now - r.WarnSince > 60f)
            {
                if (r.Quiet > 0) FiresLogger.LogWarning($"[FiresVoxel] anchor guard: {r.Quiet} more write(s) from peer {uid} put back in the last minute (not listed one by one)");
                r.WarnSince = now; r.Warns = 0; r.Quiet = 0;
            }
            if (r.Warns < 3) { r.Warns++; FiresLogger.LogWarning(line); }
            else r.Quiet++;
        }

        private static int s_accepted, s_rejected, s_adminWrites, s_droppedSamples;
        private static float s_countsAt = -1f;

        private static void NoteCounts(float now)
        {
            if (s_countsAt < 0f) { s_countsAt = now; return; }
            if (now - s_countsAt < 60f) return;
            FiresLogger.LogDebug($"[FiresVoxel] anchor guard: accepted {s_accepted} non-admin write(s), rejected {s_rejected} ({s_droppedSamples} sample(s) put back), "
                + $"{s_adminWrites} admin write(s) unchecked, in the last {now - s_countsAt:0} s");
            s_accepted = s_rejected = s_adminWrites = s_droppedSamples = 0;
            s_countsAt = now;
        }
    }
}
