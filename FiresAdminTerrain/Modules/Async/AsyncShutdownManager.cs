using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Async
{
    /// <summary>
    /// Centralized registry for all background workers.
    /// Call <see cref="ShutdownAll"/> on world unload / application quit
    /// to ensure all background threads are stopped cleanly.
    /// 
    /// Workers auto-register on construction and auto-unregister on shutdown,
    /// so consumers typically don't need to interact with this class directly.
    /// </summary>
    public static class AsyncShutdownManager
    {
        private static readonly List<IAsyncWorker> _workers = new List<IAsyncWorker>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Registers a worker for centralized shutdown.
        /// Called automatically by <see cref="BackgroundWorker{TInput,TOutput}"/> constructor.
        /// </summary>
        public static void Register(IAsyncWorker worker)
        {
            if (worker == null) return;
            lock (_lock)
            {
                if (!_workers.Contains(worker))
                {
                    _workers.Add(worker);
                }
            }
        }

        /// <summary>
        /// Unregisters a worker (e.g., after individual shutdown).
        /// Called automatically by <see cref="BackgroundWorker{TInput,TOutput}.Shutdown"/>.
        /// </summary>
        public static void Unregister(IAsyncWorker worker)
        {
            if (worker == null) return;
            lock (_lock)
            {
                _workers.Remove(worker);
            }
        }

        /// <summary>
        /// Shuts down ALL registered background workers.
        /// Call this from Game.Logout or ZNet.Shutdown patches.
        /// </summary>
        public static void ShutdownAll()
        {
            IAsyncWorker[] snapshot;
            lock (_lock)
            {
                snapshot = _workers.ToArray();
            }

            int count = 0;
            foreach (var worker in snapshot)
            {
                if (worker == null) continue;
                try
                {
                    worker.Shutdown();
                    count++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[AsyncShutdownManager] Failed to shutdown worker '{worker.Name}': {ex.Message}");
                }
            }

            lock (_lock)
            {
                _workers.Clear();
            }

            if (count > 0)
            {
                Debug.Log($"[AsyncShutdownManager] Shut down {count} background workers");
            }
        }

        /// <summary>
        /// Returns the number of currently registered workers (for debugging).
        /// </summary>
        public static int WorkerCount
        {
            get
            {
                lock (_lock)
                {
                    return _workers.Count;
                }
            }
        }
    }
}
