using System;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1346 (Fire: never two meshes; PLAN_fat_zone_swap_20261102 section A): the 1334 hold covered only the zone's chunks. Water sheets
    // and shore pieces of a zone with a swap coming now wait too and appear in the swap's own frame (VoxelZoneStreamer.TrySwap). The safety is
    // the 1334 rule: no swap coming, or EditHideMaxSeconds in reach with no swap, releases them (a hidden thing forever is worse than a double mesh).
    // Key off: nothing is held here, the 1345 path. Main thread.
    internal static class ZoneReveal
    {
        private sealed class Entry
        {
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Action> Later = new List<Action>();
            public float Since;
            public float NearSince = -1f;
            public float KeptSince = -1f;   // 0.2.1354 (review: water hidden forever outside wrap reach)
            public bool Reached;            // 0.2.1354: held with a centre known, so the zone was really in reach; a zone held only in the login window (no centre yet) is never kept
        }

        private static readonly Dictionary<Vector2s, Entry> s_held = new Dictionary<Vector2s, Entry>();
        private static readonly List<Vector2s> s_scratch = new List<Vector2s>();
        private static readonly FatSpreadStat s_stat = new FatSpreadStat("zone reveal release");
        private static float s_checkAt, s_logAt;
        private static int s_nSwap, s_nSafety, s_nItems;
        private static bool s_proof;

        internal static bool On => FiresVoxelConfig.ZoneRevealHold != null && FiresVoxelConfig.ZoneRevealHold.Value;
        internal static bool Holds(Vector2s zone) => On && (VoxelZoneStreamer.RevealHolds(zone) || KeptOutOfReach(zone));

        // 0.2.1353 (Fire: "get this terrain loading thing fixed"; 45 releases a log said "no swap is coming" for a zone with one coming): a zone already holding that falls out of
        // wrap reach keeps holding until its swap or until it unloads, and anything new of that zone joins it. A zone that never held still draws out there, as in 1346. Off: 1352.
        private static bool KeepOn => FiresVoxelConfig.ZoneRevealKeepsOutOfReach != null && FiresVoxelConfig.ZoneRevealKeepsOutOfReach.Value;
        private static bool KeptOutOfReach(Vector2s zone)
            => KeepOn && s_held.TryGetValue(zone, out var held) && held.Reached && VoxelZoneStreamer.RevealSwapStillComing(zone) && VoxelZoneStreamer.RevealZoneLoaded(zone);   // 0.2.1354 (review: water hidden forever outside wrap reach): Reached
        private static int s_nKept;
        private static bool s_keepProof;

        private static Entry For(Vector2s zone)
        {
            if (!s_held.TryGetValue(zone, out var e)) { e = new Entry { Since = Time.realtimeSinceStartup }; s_held[zone] = e; }
            if (VoxelZoneStreamer.RevealHasCentre) e.Reached = true;   // 0.2.1354 (review: water hidden forever outside wrap reach)
            if (!s_proof) { s_proof = true; FiresLogger.LogInfo("[FiresVoxel] a zone mid-swap holds its water and shore pieces; they appear in the swap frame (0.2.1346)"); }
            return e;
        }

        // A renderer stays off until the zone's release; its object can still build, rebuild or be dropped meanwhile.
        internal static bool HoldRenderer(Vector2s zone, Renderer r)
        {
            if (r == null || !Holds(zone)) return false;
            var e = For(zone);
            if (r.enabled) { r.enabled = false; e.Renderers.Add(r); }
            return true;
        }

        // Work that places something in the zone runs at the release, on the swapped ground.
        internal static bool Queue(Vector2s zone, Action later)
        {
            if (!Holds(zone)) return false;
            For(zone).Later.Add(later);
            return true;
        }

        // From TrySwap (why = null) and the safety below.
        internal static void Release(Vector2s zone, string why)
        {
            if (!s_held.TryGetValue(zone, out var e)) return;
            s_held.Remove(zone);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var r in e.Renderers) if (r != null) r.enabled = true;
            foreach (var a in e.Later)
            {
                try { a(); }
                catch (Exception ex) { FiresLogger.LogWarning($"[FiresVoxel] zone ({zone.x},{zone.y}) reveal: a held step threw: {ex.Message}"); }
            }
            s_stat.Note((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            s_nItems += e.Renderers.Count + e.Later.Count;
            if (why == null) s_nSwap++;
            else
            {
                s_nSafety++;
                FiresLogger.LogDebug($"[FiresVoxel] zone ({zone.x},{zone.y}): {e.Renderers.Count} held water renderer(s), {e.Later.Count} held step(s) released after {Time.realtimeSinceStartup - e.Since:0.0} s without a swap: {why} (0.2.1346)");
            }
        }

        // From VoxelZoneStreamer.TickEditHides, ahead of the streamer's early returns.
        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (FiresLogger.DebugEnabled && now >= s_logAt)
            {
                s_logAt = now + 10f;
                if (s_nSwap + s_nSafety > 0 || s_held.Count > 0)
                    FiresLogger.LogDebug($"[FiresVoxel] zone reveal 10 s: {s_nSwap} released in the swap frame, {s_nSafety} by the safety, {s_nItems} item(s), {s_held.Count} zone(s) holding (0.2.1346), {s_nKept} check(s) kept a zone out of reach (0.2.1353)");
                s_nSwap = s_nSafety = s_nItems = s_nKept = 0;
            }
            if (s_held.Count == 0 || now < s_checkAt) return;
            s_checkAt = now + 1f;
            s_scratch.Clear();
            s_scratch.AddRange(s_held.Keys);
            foreach (var zone in s_scratch)
            {
                var e = s_held[zone];
                if (!On) { Release(zone, "the key was turned off"); continue; }
                if (!VoxelZoneStreamer.RevealHolds(zone))
                {
                    // 0.2.1353
                    if (KeptOutOfReach(zone))
                    {
                        s_nKept++;
                        e.NearSince = -1f;
                        // 0.2.1354 (review: water hidden forever outside wrap reach): the keep had no clock of its own and reset the in-reach one. No GiveUpHide: the swap is still good when the player returns.
                        if (e.KeptSince < 0f) e.KeptSince = now;
                        if (now - e.KeptSince > VoxelZoneStreamer.EditHideSeconds)
                        {
                            Release(zone, $"kept {VoxelZoneStreamer.EditHideSeconds:0} s out of reach with no swap");
                            continue;
                        }
                        if (!s_keepProof) { s_keepProof = true; FiresLogger.LogInfo("[FiresVoxel] a zone holding its water that falls out of reach keeps holding until its swap, until it unloads, or for EditHideSeconds (0.2.1353, 0.2.1354)"); }
                        continue;
                    }
                    Release(zone, !KeepOn || !VoxelZoneStreamer.RevealSwapStillComing(zone) ? "no swap is coming" : e.Reached ? "it fell out of reach and unloaded" : "held at login before a centre was known, and it is out of reach (0.2.1354)");
                    continue;
                }
                if (VoxelZoneStreamer.RevealHasCentre) { e.Reached = true; e.KeptSince = -1f; }   // 0.2.1354 (review: water hidden forever outside wrap reach)
                if (VoxelZoneStreamer.RevealInReach(zone))
                {
                    if (e.NearSince < 0f) e.NearSince = now;
                    if (now - e.NearSince > VoxelZoneStreamer.EditHideSeconds)
                    {
                        VoxelZoneStreamer.GiveUpHide(zone);
                        Release(zone, $"its swap has not landed {VoxelZoneStreamer.EditHideSeconds:0} s with the player in reach");
                    }
                }
                else e.NearSince = -1f;
            }
        }

        // 0.2.1354 (review: statics surviving logout): called from WaterShapingTools.ForgetSession. Held steps are dropped, never run: they would place pieces at the old world's coordinates in the next login.
        internal static void Clear()
        {
            foreach (var e in s_held.Values)
                foreach (var r in e.Renderers)
                    if (r != null) r.enabled = true;
            s_held.Clear();
            s_scratch.Clear();
        }
    }
}
