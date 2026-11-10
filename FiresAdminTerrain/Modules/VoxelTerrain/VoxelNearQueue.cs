using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // 0.2.1344 (Fire's rule: "fill from closest out"; r22: far zones' chunks built and landed ahead of the ground under him).
    // The camera / player positions the build queues measure against. Written on the main thread (VoxelMeshPipeline's driver), read
    // by the worker threads: plain floats behind a volatile count; a torn read only orders one pick slightly differently.
    internal static class VoxelNearOrder
    {
        internal const int MaxCenters = 8;
        private static readonly float[] s_x = new float[MaxCenters], s_y = new float[MaxCenters], s_z = new float[MaxCenters];
        private static volatile int s_count;
        private static readonly List<Vector3> s_scratch = new List<Vector3>(MaxCenters);

        internal static bool On => FiresVoxelConfig.BuildNearestFirst != null && FiresVoxelConfig.BuildNearestFirst.Value;

        // Main thread, every driver frame.
        internal static void Publish()
        {
            s_scratch.Clear();
            var player = Player.m_localPlayer;
            if (player != null) s_scratch.Add(player.transform.position);
            var cam = GameCamera.instance;
            if (cam != null) s_scratch.Add(cam.transform.position);
            if (s_scratch.Count == 0) VoxelZoneStreamer.CopyCenters(s_scratch);   // a dedicated server: its peers; a client before it spawns: the pending spawn
            int n = Mathf.Min(s_scratch.Count, MaxCenters);
            for (int i = 0; i < n; i++) { var p = s_scratch[i]; s_x[i] = p.x; s_y[i] = p.y; s_z[i] = p.z; }
            s_count = n;
        }

        // Squared metres to the nearest published position; 0 when none is published yet.
        internal static float DistSq(float x, float y, float z)
        {
            int n = s_count;
            if (n == 0) return 0f;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float dx = s_x[i] - x, dy = s_y[i] - y, dz = s_z[i] - z;
                float d = dx * dx + dy * dy + dz * dz;
                if (d < best) best = d;
            }
            return best;
        }
    }

    // A build queue that hands out the pending item nearest the player. Key off: the 1343 FIFO queue. A pick is a linear scan of at most
    // ScanCap entries under a short lock, with no allocation (a mesh job is milliseconds; the scan is microseconds); past ScanCap the
    // window moves on each pick, so every entry is seen. Equal distance = the older entry, so two results of ONE chunk keep their order.
    // No starvation: every 8th pick takes the oldest entry if it has waited AgeSeconds. A far item drains at 1/8 of the throughput at worst.
    internal sealed class VoxelNearQueue<T> where T : class
    {
        private struct Entry { public T Item; public VoxelChunk Chunk; public float X, Y, Z; public long Seq, Ticks; }
        private const int ScanCap = 1024;
        private const double AgeSeconds = 3.0;
        private static readonly long AgeTicks = (long)(AgeSeconds * Stopwatch.Frequency);

        private readonly ConcurrentQueue<T> _fifo = new ConcurrentQueue<T>();
        private readonly bool _trackChunks;
        private readonly Dictionary<VoxelChunk, int> _held;
        private readonly bool _stampPicks;   // 0.2.1353: a pick leaves its stamps on the chunk, for EnqueueKeepAge
        private readonly object _lock = new object();
        private Entry[] _items = new Entry[256];
        private volatile int _count;
        private int _cursor, _picks;
        private long _seq;

        // Stats (under _lock), taken by the driver's 10 s line.
        private long _sPicks, _sAged, _sScanTicks, _sWorstTicks;
        private double _sSumDist;
        private float _sMaxDist;
        private int _sWorstBacklog;

        internal VoxelNearQueue(bool trackChunks = false, bool stampPicks = false)
        {
            _trackChunks = trackChunks;
            _stampPicks = stampPicks;
            if (trackChunks) _held = new Dictionary<VoxelChunk, int>();
        }

        internal int Count => _count + _fifo.Count;
        internal bool IsEmpty => _count == 0 && _fifo.IsEmpty;

        internal void Enqueue(T item, VoxelChunk chunk)
        {
            if (!VoxelNearOrder.On) { _fifo.Enqueue(item); return; }
            Insert(item, chunk, -1L, Stopwatch.GetTimestamp());
        }

        // 0.2.1353 (Fire: "get this terrain loading thing fixed"; 600-700 starved retries a second): a worker that picked this chunk and found no buffer puts it back with the
        // Seq and Ticks of that pick. A fresh Seq made it the newest entry on every retry, and the oldest-first turn (every 8th pick) chooses by Seq and then tests Ticks, so a
        // chunk starving near the back was never the oldest and its age never passed AgeSeconds. False = no stamps to keep (the FIFO, or it came from the urgent lane): an ordinary enqueue.
        internal bool EnqueueKeepAge(T item, VoxelChunk chunk)
        {
            if (!VoxelNearOrder.On || chunk == null || chunk.PickTicks == 0L) { Enqueue(item, chunk); return false; }
            Insert(item, chunk, chunk.PickSeq, chunk.PickTicks);
            return true;
        }

        private void Insert(T item, VoxelChunk chunk, long seq, long ticks)
        {
            Vector3 o = chunk != null ? chunk.WorldOrigin : Vector3.zero;
            float half = VoxelChunk.CELLS * 0.5f;
            lock (_lock)
            {
                if (_count == _items.Length) System.Array.Resize(ref _items, _items.Length * 2);
                _items[_count] = new Entry { Item = item, Chunk = chunk, X = o.x + half, Y = o.y + half, Z = o.z + half, Seq = seq >= 0L ? seq : _seq++, Ticks = ticks };
                _count = _count + 1;
                if (_count > _sWorstBacklog) _sWorstBacklog = _count;
                if (_trackChunks && chunk != null) { _held.TryGetValue(chunk, out int n); _held[chunk] = n + 1; }
            }
        }

        // A result of this chunk is waiting here (near mode only; the 1343 FIFO never knew).
        internal bool Holds(VoxelChunk chunk)
        {
            if (!_trackChunks || chunk == null || _count == 0) return false;
            lock (_lock) return _held.ContainsKey(chunk);
        }

        internal bool TryDequeue(out T item)
        {
            if (_count > 0)
            {
                long t0 = Stopwatch.GetTimestamp();
                lock (_lock)
                {
                    int count = _count;
                    if (count > 0)
                    {
                        int window = Mathf.Min(count, ScanCap);
                        int start = count > ScanCap ? _cursor % count : 0;
                        int best = -1, oldest = -1;
                        float bestD = float.MaxValue;
                        long bestSeq = long.MaxValue, oldestSeq = long.MaxValue;
                        for (int w = 0; w < window; w++)
                        {
                            int i = start + w; if (i >= count) i -= count;
                            float d = VoxelNearOrder.DistSq(_items[i].X, _items[i].Y, _items[i].Z);
                            long seq = _items[i].Seq;
                            if (d < bestD || (d == bestD && seq < bestSeq)) { bestD = d; bestSeq = seq; best = i; }
                            if (seq < oldestSeq) { oldestSeq = seq; oldest = i; }
                        }
                        if (count > ScanCap) _cursor = start + window;
                        bool aged = (++_picks & 7) == 0 && oldest != best && t0 - _items[oldest].Ticks > AgeTicks;
                        int pick = aged ? oldest : best;
                        item = _items[pick].Item;
                        var chunk = _items[pick].Chunk;
                        if (_stampPicks && chunk != null) { chunk.PickSeq = _items[pick].Seq; chunk.PickTicks = _items[pick].Ticks; }   // 0.2.1353
                        float dist = Mathf.Sqrt(VoxelNearOrder.DistSq(_items[pick].X, _items[pick].Y, _items[pick].Z));
                        if (_trackChunks && chunk != null && _held.TryGetValue(chunk, out int n))
                        {
                            if (n <= 1) _held.Remove(chunk); else _held[chunk] = n - 1;
                        }
                        _items[pick] = _items[count - 1];
                        _items[count - 1] = default(Entry);
                        _count = count - 1;
                        _sPicks++; if (aged) _sAged++;
                        _sSumDist += dist; if (dist > _sMaxDist) _sMaxDist = dist;
                        long spent = Stopwatch.GetTimestamp() - t0;
                        _sScanTicks += spent; if (spent > _sWorstTicks) _sWorstTicks = spent;
                        return true;
                    }
                }
            }
            return _fifo.TryDequeue(out item);
        }

        // One stats clause for the driver's 10 s line; empty when nothing was picked. Resets.
        internal string TakeStats(string name)
        {
            lock (_lock)
            {
                if (_sPicks == 0) return "";
                double f = 1000.0 / Stopwatch.Frequency;
                string s = $"{name} {_sPicks} pick(s), {_sAged} oldest-first, scan total {_sScanTicks * f:0.00} ms worst {_sWorstTicks * f:0.000} ms, distance avg {_sSumDist / _sPicks:0} m max {_sMaxDist:0} m, backlog worst {_sWorstBacklog}; ";
                _sPicks = _sAged = _sScanTicks = _sWorstTicks = 0; _sSumDist = 0; _sMaxDist = 0f; _sWorstBacklog = _count;
                return s;
            }
        }
    }
}
