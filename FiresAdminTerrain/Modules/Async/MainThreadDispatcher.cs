using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Async
{
    /// <summary>
    /// Dispatches actions from background threads to the Unity main thread.
    /// 
    /// Attach to a hidden, persistent GameObject. Actions are dequeued and
    /// executed in Update() on the main thread, making it safe to call
    /// Unity APIs (Mesh, Texture2D, Material, Physics, Transform, etc.).
    /// 
    /// <para><b>Time Budget:</b> To prevent network disconnects (flashing icon)
    /// caused by blocking the main thread too long, Update() enforces a per-frame
    /// time budget. Actions that exceed the budget are deferred to the next frame.
    /// This keeps the main thread responsive so ZNet can process heartbeats.</para>
    /// 
    /// Usage from any thread:
    ///   MainThreadDispatcher.Enqueue(() => { /* runs on main thread */ });
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly Queue<Action> _actionQueue = new Queue<Action>();
        private static readonly object _queueLock = new object();

        // Actions pulled from the shared queue into this local list for lock-free processing.
        // Leftover actions from budget exhaustion carry over to the next frame.
        private readonly List<Action> _processingList = new List<Action>();
        private int _processingIndex;
        private readonly Stopwatch _frameStopwatch = new Stopwatch();

        /// <summary>
        /// Maximum milliseconds to spend executing dispatched actions per frame.
        /// Keeps the main thread responsive for network heartbeats and rendering.
        /// Default 4ms - leaves ~12ms of a 60fps frame for game logic and rendering.
        /// </summary>
        public static float MaxBudgetMs = 4f;

        /// <summary>
        /// Enqueues an action to be executed on the next main-thread Update().
        /// Thread-safe - can be called from any thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;

            lock (_queueLock)
            {
                _actionQueue.Enqueue(action);
            }

            EnsureInstance();
        }

        /// <summary>
        /// Returns the number of actions currently waiting to be processed.
        /// </summary>
        public static int PendingCount
        {
            get
            {
                lock (_queueLock)
                {
                    int queued = _actionQueue.Count;
                    if (_instance != null)
                        queued += _instance._processingList.Count - _instance._processingIndex;
                    return queued;
                }
            }
        }

        /// <summary>
        /// Starts a Unity coroutine on the dispatcher's hidden GameObject.
        /// MUST be called from the main thread. Use this when you need true
        /// frame-by-frame pacing (e.g. <c>yield return null</c>) - the normal
        /// <see cref="Enqueue"/> path will drain many actions in a single frame
        /// because it runs on a time budget, which is unsuitable for back-pressure
        /// sensitive work like chunked network sends.
        /// </summary>
        public static Coroutine StartRoutine(IEnumerator routine)
        {
            if (routine == null) return null;
            EnsureInstance();
            return _instance.StartCoroutine(routine);
        }

        private static void EnsureInstance()
        {
            if (_instance != null) return;

            var go = new GameObject("VA_MainThreadDispatcher");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }

        private void Update()
        {
            // Pull new actions from the shared queue into our local processing list.
            // This minimises time spent under the lock and avoids blocking background threads.
            lock (_queueLock)
            {
                while (_actionQueue.Count > 0)
                {
                    _processingList.Add(_actionQueue.Dequeue());
                }
            }

            if (_processingIndex >= _processingList.Count)
            {
                // Nothing to process - reset for next frame.
                _processingList.Clear();
                _processingIndex = 0;
                return;
            }

            _frameStopwatch.Reset();
            _frameStopwatch.Start();

            float budgetMs = MaxBudgetMs;

            // Always process at least one action per frame to guarantee forward progress,
            // even if a single action exceeds the budget.
            bool processedFirst = false;

            while (_processingIndex < _processingList.Count)
            {
                // After the first action, respect the time budget.
                if (processedFirst && _frameStopwatch.Elapsed.TotalMilliseconds >= budgetMs)
                    break;

                var action = _processingList[_processingIndex++];
                processedFirst = true;
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[MainThreadDispatcher] Action failed: {ex.Message}");
                }
            }

            _frameStopwatch.Stop();

            // If we've processed everything, clear the list for next frame.
            if (_processingIndex >= _processingList.Count)
            {
                _processingList.Clear();
                _processingIndex = 0;
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
