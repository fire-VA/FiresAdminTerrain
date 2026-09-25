using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace VerdantsAscent.Async
{
    /// <summary>
    /// Callback invoked on the main thread when a background job completes.
    /// </summary>
    public delegate void AsyncJobCallback<TOutput>(TOutput output);

    /// <summary>
    /// Interface for workers that can be registered with <see cref="AsyncShutdownManager"/>.
    /// </summary>
    public interface IAsyncWorker
    {
        void Shutdown();
        bool HasPendingJobs { get; }
        string Name { get; }
    }

    /// <summary>
    /// A generic single-thread background worker that processes jobs of type TInput -> TOutput.
    /// 
    /// Consumers provide a processing function; the worker handles threading, queuing, 
    /// callback dispatch, and shutdown.
    /// 
    /// <para><b>Thread Safety:</b> QueueJob() is thread-safe. The processing function runs
    /// on a dedicated background thread. Callbacks are dispatched to the main thread via
    /// <see cref="MainThreadDispatcher"/>.</para>
    /// 
    /// <para><b>Lifecycle:</b> The background thread is created lazily on the first QueueJob()
    /// call and destroyed on Shutdown(). The worker auto-registers with
    /// <see cref="AsyncShutdownManager"/> so all workers can be shut down centrally on logout.</para>
    /// 
    /// <para><b>Constraints (.NET 4.8 / Unity Mono):</b> Uses Thread + Monitor (no Task/async).
    /// The processing function must NOT call Unity API - only pure math/data operations.</para>
    /// 
    /// Usage:
    /// <code>
    ///   var worker = new BackgroundWorker&lt;MyInput, MyOutput&gt;("TerrainMesh", ProcessJob);
    ///   worker.QueueJob(input, output => { /* main thread callback */ });
    ///   // ...
    ///   worker.Shutdown(); // or let AsyncShutdownManager handle it
    /// </code>
    /// </summary>
    public class BackgroundWorker<TInput, TOutput> : IAsyncWorker
        where TInput : class
        where TOutput : class, new()
    {
        private readonly string _name;
        private readonly Action<TInput, TOutput> _processFunc;
        private readonly Queue<PendingJob> _pendingJobs = new Queue<PendingJob>();
        private readonly object _jobLock = new object();
        private Thread _workerThread;
        private volatile bool _shutdownRequested;
        private int _activeJobCount;

        private class PendingJob
        {
            public TInput Input;
            public TOutput Output;
            public AsyncJobCallback<TOutput> Callback;
        }

        /// <summary>
        /// Creates a new background worker.
        /// </summary>
        /// <param name="name">Thread name (for debugging)</param>
        /// <param name="processFunc">
        /// Function that processes a job on the background thread.
        /// Receives (input, output) - must populate output fields.
        /// Must NOT call Unity API.
        /// </param>
        public BackgroundWorker(string name, Action<TInput, TOutput> processFunc)
        {
            _name = name ?? "BackgroundWorker";
            _processFunc = processFunc ?? throw new ArgumentNullException(nameof(processFunc));
            AsyncShutdownManager.Register(this);
        }

        /// <summary>
        /// Worker name (for debugging/logging).
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Returns true if there are pending or in-progress jobs.
        /// </summary>
        public bool HasPendingJobs => _activeJobCount > 0;

        /// <summary>
        /// Queues a job for background processing.
        /// Thread-safe - can be called from any thread (typically main thread).
        /// </summary>
        /// <param name="input">Job input data (snapshotted on caller's thread)</param>
        /// <param name="callback">Callback invoked on main thread when job completes</param>
        public void QueueJob(TInput input, AsyncJobCallback<TOutput> callback)
        {
            if (input == null || callback == null) return;

            EnsureWorkerThread();

            var job = new PendingJob
            {
                Input = input,
                Output = new TOutput(),
                Callback = callback
            };

            lock (_jobLock)
            {
                _pendingJobs.Enqueue(job);
                Interlocked.Increment(ref _activeJobCount);
                Monitor.Pulse(_jobLock);
            }
        }

        /// <summary>
        /// Shuts down the worker thread. Waits up to 1 second for the thread to finish.
        /// Safe to call multiple times.
        /// </summary>
        public void Shutdown()
        {
            _shutdownRequested = true;

            lock (_jobLock)
            {
                Monitor.PulseAll(_jobLock);
            }

            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(1000);
            }

            _workerThread = null;
            _shutdownRequested = false;
            _activeJobCount = 0;

            lock (_jobLock)
            {
                _pendingJobs.Clear();
            }

            AsyncShutdownManager.Unregister(this);
        }

        private void EnsureWorkerThread()
        {
            if (_workerThread != null && _workerThread.IsAlive)
                return;

            _shutdownRequested = false;
            _workerThread = new Thread(WorkerLoop)
            {
                Name = $"VA_{_name}",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            _workerThread.Start();
        }

        private void WorkerLoop()
        {
            while (!_shutdownRequested)
            {
                PendingJob job = null;

                lock (_jobLock)
                {
                    while (_pendingJobs.Count == 0 && !_shutdownRequested)
                    {
                        Monitor.Wait(_jobLock);
                    }

                    if (_shutdownRequested) break;

                    if (_pendingJobs.Count > 0)
                    {
                        job = _pendingJobs.Dequeue();
                    }
                }

                if (job != null)
                {
                    try
                    {
                        _processFunc(job.Input, job.Output);

                        // Dispatch callback to main thread via FrameBudgetScheduler.
                        // This ensures background worker callbacks share the same
                        // per-frame time budget as all other mod work, preventing
                        // the dual-budget problem where MainThreadDispatcher (4ms)
                        // and FrameBudgetScheduler (5ms) independently consumed
                        // up to 9ms/frame combined.
                        var callback = job.Callback;
                        var output = job.Output;
                        FrameBudgetScheduler.Schedule(WorkPriority.High, () =>
                        {
                            try
                            {
                                callback(output);
                            }
                            catch (Exception ex)
                            {
                                FiresLogger.LogWarning($"[{_name}] Callback failed: {ex.Message}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"[{_name}] Job failed: {ex.Message}\n{ex.StackTrace}");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeJobCount);
                    }
                }
            }
        }
    }
}
