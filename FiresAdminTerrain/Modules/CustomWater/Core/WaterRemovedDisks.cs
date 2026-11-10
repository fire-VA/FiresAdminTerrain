using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Water.Core
{
    // 0.2.1336 (mop removals persist). Two halves, both needed:
    // 1. The anchor ZDO is written directly on the server (WaterMeshCombinerAnchor.RemoveDisksDirect): ZDOs persist unloaded, and a
    //    dedicated server rarely has the zone loaded. No queue: a queue would wait for a load the ZDO does not need.
    // 2. A list of removed hashes, kept in the world's records on the server and sent to each joining peer: when the removal emptied a
    //    combiner its anchor is destroyed, so nothing is left in the world to tell an offline client's cache file (1331's anchor-newer
    //    stamp needs an anchor to compare). Re-creating a hash takes it off the list.
    // Requests in one frame share one ZDO scan.
    internal static class WaterRemovedDisks
    {
        internal const int MaxHashes = 100000;   // 400 KB, inside SafeRoutedRpc's 480 KB
        private const int FileMagic = 0x44524D57;
        private const string Suffix = "_water_removed.bin";

        private static readonly HashSet<int> s_set = new HashSet<int>();
        private static readonly List<int> s_order = new List<int>();   // oldest first; server only
        private static readonly Dictionary<int, Vector3?> s_pending = new Dictionary<int, Vector3?>();
        private static string s_loadedFor;
        private static bool s_flushQueued, s_fileDirty, s_infoSaid;

        internal static bool Contains(int hash) { EnsureLoaded(); return s_set.Contains(hash); }

        internal static void AddLocal(int hash) => s_set.Add(hash);
        internal static void ForgetLocal(int hash) => s_set.Remove(hash);

        internal static List<int> ServerList() { EnsureLoaded(); return new List<int>(s_order); }

        internal static void NoteOnServer(int hash, Vector3? at)
        {
            EnsureLoaded();
            s_set.Add(hash);
            if (!s_order.Contains(hash))
            {
                s_order.Add(hash);
                if (s_order.Count > MaxHashes) { s_set.Remove(s_order[0]); s_order.RemoveAt(0); }
                s_fileDirty = true;
            }
            s_pending[hash] = at;
            QueueFlush();
        }

        // 0.2.1354 (review: permanent disk loss): a removal that does not come through ServerHandleRemoveAndBroadcast. SaveCombinerDirect and TrySaveToZDO keep what an anchor holds that its combiner lacks unless it is
        // on this list, so every removal must land here on the server, before its save. Clients keep their own lists from the server, so they note nothing.
        internal static void NoteRemovedHere(WaterDisk disk)
        {
            if (disk == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            NoteOnServer(disk.StableHash, disk.Position);
        }

        internal static void ForgetOnServer(int hash)
        {
            EnsureLoaded();
            s_set.Remove(hash);
            s_pending.Remove(hash);
            if (s_order.Remove(hash)) { s_fileDirty = true; QueueFlush(); }
        }

        internal static void Clear()
        {
            s_set.Clear(); s_order.Clear(); s_pending.Clear();
            s_loadedFor = null; s_fileDirty = false;
        }

        private static void QueueFlush()
        {
            if (s_flushQueued) return;
            s_flushQueued = true;
            VerdantsAscent.Async.MainThreadDispatcher.Enqueue(Flush);
        }

        private static void Flush()
        {
            s_flushQueued = false;
            try
            {
                if (s_pending.Count > 0)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    int asked = s_pending.Count;
                    var pending = new Dictionary<int, Vector3?>(s_pending);
                    s_pending.Clear();
                    WaterMeshCombinerAnchor.RemoveDisksDirect(pending, out int anchors, out int disks, out int destroyed);
                    FiresLogger.LogDebug($"[WaterRemovedDisks] {asked} removal(s): {disks} disk(s) taken out of {anchors} anchor ZDO(s), {destroyed} emptied and destroyed, in {clock.ElapsedMilliseconds} ms");
                    if (!s_infoSaid) { s_infoSaid = true; FiresLogger.LogInfo("[WaterRemovedDisks] water removals persist: the server writes the anchor ZDO directly, joining clients get the removed list for their cache files (0.2.1336)"); }
                }
                if (s_fileDirty) Save();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[WaterRemovedDisks] flush failed: {ex.Message}"); }
        }

        private static string PathNow() => FiresWorldRecords.PathFor(ZNet.instance.GetWorldName(), Suffix);

        private static void EnsureLoaded()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            string world = ZNet.instance.GetWorldName();
            if (string.IsNullOrEmpty(world) || s_loadedFor == world) return;
            s_loadedFor = world;
            try
            {
                string path = PathNow();
                if (!File.Exists(path)) return;
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != FileMagic) return;
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxHashes || count * 4L > r.BaseStream.Length - r.BaseStream.Position)
                    {
                        FiresLogger.LogWarning($"[WaterRemovedDisks] {path}: bad count {count}; list skipped");
                        return;
                    }
                    for (int i = 0; i < count; i++) { int h = r.ReadInt32(); if (s_set.Add(h) || !s_order.Contains(h)) s_order.Add(h); }
                }
                FiresLogger.LogDebug($"[WaterRemovedDisks] {s_order.Count} removed hash(es) read for '{world}'");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"[WaterRemovedDisks] could not read the list: {ex.Message}"); }
        }

        private static void Save()
        {
            s_fileDirty = false;
            string path = PathNow(), tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(FileMagic);
                w.Write(s_order.Count);
                foreach (int h in s_order) w.Write(h);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
