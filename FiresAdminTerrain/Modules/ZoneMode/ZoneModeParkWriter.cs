using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace VerdantsAscent.Terrain.Zones
{
    // The park's zones, appended to one pack file (ZoneModeParkPack) on a background thread while the main thread
    // serialises the next ones. The bytes are exactly what the park serialised, framed and checksummed on this thread, into
    // the staging folder; the park commits that folder only after Finish returns, so a failed write still leaves the world
    // unparked. Memory stays bounded: Enqueue waits while MaxBytesInFlight are queued and not yet written.
    internal sealed class ZoneModeParkWriter : IDisposable
    {
        private const int MaxBytesInFlight = 64 << 20;
        private const string ThreadName = "FiresAdminTerrain park writer";

        private struct Pending
        {
            internal Vector2s Zone;
            internal ZoneModePark.ParkedZone Parked;
            internal byte[] Bytes;
        }

        private readonly Queue<Pending> _pending = new Queue<Pending>();
        private readonly List<KeyValuePair<Vector2s, ZoneModeParkPack.Record>> _written = new List<KeyValuePair<Vector2s, ZoneModeParkPack.Record>>();
        private readonly object _gate = new object();
        private readonly Stopwatch _waited = new Stopwatch();
        private readonly ZoneModeParkPack.FrameStream _frames;
        private readonly Thread _thread;
        private long _bytesInFlight;
        private bool _closed;
        private Exception _failure;

        internal int ZonesWritten { get; private set; }
        internal long BytesWritten { get; private set; }
        internal long WaitedMs => _waited.ElapsedMilliseconds;

        // Where each zone's frame landed, in the order written. Read it after Finish.
        internal List<KeyValuePair<Vector2s, ZoneModeParkPack.Record>> Written => _written;

        internal ZoneModeParkWriter(string packPath, long worldUid)
        {
            _frames = ZoneModeParkPack.FrameStream.Create(packPath, worldUid);
            _thread = new Thread(Run) { IsBackground = true, Name = ThreadName };
            _thread.Start();
        }

        internal void Enqueue(Vector2s zone, ZoneModePark.ParkedZone parked, byte[] bytes)
        {
            lock (_gate)
            {
                if (_failure == null && _bytesInFlight > 0 && _bytesInFlight + bytes.Length > MaxBytesInFlight)
                {
                    _waited.Start();
                    while (_failure == null && _bytesInFlight > 0 && _bytesInFlight + bytes.Length > MaxBytesInFlight) Monitor.Wait(_gate);
                    _waited.Stop();
                }
                ThrowIfFailed();
                _pending.Enqueue(new Pending { Zone = zone, Parked = parked, Bytes = bytes });
                _bytesInFlight += bytes.Length;
                Monitor.PulseAll(_gate);
            }
        }

        // Every queued zone is written, flushed to disk and the pack closed when this returns; a write that failed is
        // thrown here, on the main thread.
        internal void Finish()
        {
            _waited.Start();
            Close(dropPending: false);
            _waited.Stop();
            ThrowIfFailed();
        }

        public void Dispose() => Close(dropPending: true);

        private void Close(bool dropPending)
        {
            lock (_gate)
            {
                if (_closed && !_thread.IsAlive) return;
                _closed = true;
                if (dropPending) _pending.Clear();
                Monitor.PulseAll(_gate);
            }
            _thread.Join();
        }

        private void ThrowIfFailed()
        {
            if (_failure != null) throw new System.IO.IOException($"a parked zone could not be written: {_failure.Message}", _failure);
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    Pending next;
                    lock (_gate)
                    {
                        while (_pending.Count == 0 && !_closed) Monitor.Wait(_gate);
                        if (_pending.Count == 0) break;
                        next = _pending.Dequeue();
                    }
                    var record = _frames.Write(next.Zone, ParkSlot.Parked, next.Parked, next.Bytes);
                    lock (_gate)
                    {
                        _written.Add(new KeyValuePair<Vector2s, ZoneModeParkPack.Record>(next.Zone, record));
                        _bytesInFlight -= next.Bytes.Length;
                        ZonesWritten++;
                        BytesWritten += next.Bytes.Length;
                        Monitor.PulseAll(_gate);
                    }
                }
                _frames.Flush();
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failure = ex;
                    _pending.Clear();
                    _bytesInFlight = 0;
                    Monitor.PulseAll(_gate);
                }
            }
            finally { _frames.Dispose(); }
        }
    }
}
