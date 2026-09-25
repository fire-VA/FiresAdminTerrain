using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Async
{
    /// <summary>
    /// Priority lanes for scheduled work. Lower value = higher priority.
    /// </summary>
    public enum WorkPriority
    {
        /// <summary>Collision baking for terrain the player is standing on.</summary>
        Critical = 0,
        /// <summary>Mesh apply from background workers, RPC terrain creation.</summary>
        High = 1,
        /// <summary>Material setup, paint masks, coroutine phases.</summary>
        Normal = 2,
        /// <summary>Grass, clutter, cache I/O, LOD, cosmetic work.</summary>
        Low = 3
    }

    /// <summary>
    /// Centralized per-frame time budget scheduler for all mod operations.
    /// 
    /// Replaces the fragmented budget approach where MainThreadDispatcher (4ms),
    /// DirtFloorDeferredInit (8ms), and multiple uncoordinated coroutines each
    /// consume frame time independently, causing collective budget overruns.
    /// 
    /// All heavy main-thread work routes through this scheduler so the mod never
    /// exceeds its frame budget regardless of how many systems are active.
    /// 
    /// <para><b>Thread Safety:</b> Schedule() is thread-safe - can be called from
    /// background threads. All execution happens on the main thread in Update().</para>
    /// 
    /// <para><b>Budget:</b> Default 5ms per frame. Always processes at least one item
    /// per frame to guarantee forward progress even if a single item exceeds budget.</para>
    /// 
    /// <para><b>Priority:</b> Four lanes (Critical -> Low). Within a lane, items are
    /// processed FIFO. Higher priority lanes are drained first each frame.</para>
    /// 
    /// Usage:
    /// <code>
    ///   FrameBudgetScheduler.Schedule(WorkPriority.High, () => ApplyMeshResult(data));
    ///   FrameBudgetScheduler.ScheduleCoroutine(WorkPriority.Normal, MyCoroutine());
    /// </code>
    /// </summary>
    public class FrameBudgetScheduler : MonoBehaviour
    {
        private static FrameBudgetScheduler _instance;
        private static readonly object _queueLock = new object();

        private const int LANE_COUNT = 4;

        // One queue per priority lane, accessed under _queueLock from background threads
        private static readonly Queue<WorkItem>[] s_incomingQueues = new Queue<WorkItem>[LANE_COUNT]
        {
            new Queue<WorkItem>(),
            new Queue<WorkItem>(),
            new Queue<WorkItem>(),
            new Queue<WorkItem>()
        };

        // Local processing queues (main thread only, no lock needed)
        private readonly List<WorkItem>[] _localQueues = new List<WorkItem>[LANE_COUNT]
        {
            new List<WorkItem>(),
            new List<WorkItem>(),
            new List<WorkItem>(),
            new List<WorkItem>()
        };

        private readonly int[] _localIndex = new int[LANE_COUNT];
        private readonly Stopwatch _frameSw = new Stopwatch();

        /// <summary>
        /// Maximum milliseconds per frame for ALL scheduled mod work.
        /// Default 5ms - leaves 11ms of a 60fps frame for Valheim + rendering + network.
        /// </summary>
        public static float MaxBudgetMs = 5f;

        // Diagnostics
        private static float _lastFrameMs;
        private static bool _budgetExceeded;
        private static int _pendingCount;

        #region Public API

        /// <summary>
        /// Schedules a single action for execution within the frame budget.
        /// Thread-safe - can be called from background threads.
        /// </summary>
        public static void Schedule(WorkPriority priority, Action action)
        {
            if (action == null) return;

            int lane = (int)priority;
            if (lane < 0 || lane >= LANE_COUNT) lane = (int)WorkPriority.Normal;

            lock (_queueLock)
            {
                s_incomingQueues[lane].Enqueue(new WorkItem { Action = action });
            }

            EnsureInstance();
        }

        /// <summary>
        /// Schedules a sequence of actions that execute in order, one per frame budget slot.
        /// </summary>
        public static void ScheduleSequence(WorkPriority priority, Action[] steps)
        {
            if (steps == null || steps.Length == 0) return;

            foreach (var step in steps)
            {
                if (step != null)
                    Schedule(priority, step);
            }
        }

        /// <summary>
        /// Schedules a coroutine-style IEnumerator whose steps execute within the budget.
        /// Each yield return becomes a scheduling boundary - the scheduler picks up the
        /// next step when budget is available.
        /// </summary>
        public static void ScheduleCoroutine(WorkPriority priority, IEnumerator steps,
            Action onComplete = null)
        {
            if (steps == null) return;

            int lane = (int)priority;
            if (lane < 0 || lane >= LANE_COUNT) lane = (int)WorkPriority.Normal;

            lock (_queueLock)
            {
                s_incomingQueues[lane].Enqueue(new WorkItem
                {
                    Coroutine = steps,
                    OnComplete = onComplete
                });
            }

            EnsureInstance();
        }

        /// <summary>Number of work items waiting across all priority lanes.</summary>
        public static int PendingCount => _pendingCount;

        /// <summary>Milliseconds consumed by the scheduler in the last frame.</summary>
        public static float LastFrameMs => _lastFrameMs;

        /// <summary>True if the scheduler exceeded its budget last frame.</summary>
        public static bool BudgetExceededLastFrame => _budgetExceeded;

        #endregion

        #region Work Item

        private struct WorkItem
        {
            public Action Action;
            public IEnumerator Coroutine;
            public Action OnComplete;
        }

        #endregion

        #region Lifecycle

        private static void EnsureInstance()
        {
            if (_instance != null) return;

            var go = new GameObject("VA_FrameBudgetScheduler");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<FrameBudgetScheduler>();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        #endregion

        #region Update Loop

        private void Update()
        {
            // Pull incoming items from shared queues into local lists
            lock (_queueLock)
            {
                for (int lane = 0; lane < LANE_COUNT; lane++)
                {
                    var incoming = s_incomingQueues[lane];
                    while (incoming.Count > 0)
                    {
                        _localQueues[lane].Add(incoming.Dequeue());
                    }
                }
            }

            // Count total pending
            int total = 0;
            for (int lane = 0; lane < LANE_COUNT; lane++)
            {
                total += _localQueues[lane].Count - _localIndex[lane];
            }

            if (total == 0)
            {
                // Nothing to process - reset
                for (int lane = 0; lane < LANE_COUNT; lane++)
                {
                    _localQueues[lane].Clear();
                    _localIndex[lane] = 0;
                }
                _pendingCount = 0;
                _lastFrameMs = 0;
                _budgetExceeded = false;
                return;
            }

            _frameSw.Reset();
            _frameSw.Start();

            float budgetMs = MaxBudgetMs;
            bool processedFirst = false;
            bool budgetExhausted = false;

            // Process lanes in priority order (Critical first)
            for (int lane = 0; lane < LANE_COUNT && !budgetExhausted; lane++)
            {
                var list = _localQueues[lane];
                ref int idx = ref _localIndex[lane];

                while (idx < list.Count)
                {
                    // After the first item, respect the budget
                    if (processedFirst && _frameSw.Elapsed.TotalMilliseconds >= budgetMs)
                    {
                        budgetExhausted = true;
                        break;
                    }

                    var item = list[idx];
                    processedFirst = true;

                    if (item.Coroutine != null)
                    {
                        // Advance coroutine one step
                        bool hasMore = false;
                        try
                        {
                            hasMore = item.Coroutine.MoveNext();
                        }
                        catch (Exception ex)
                        {
                            FiresLogger.LogWarning($"[FrameBudgetScheduler] Coroutine step failed: {ex.Message}");
                        }

                        if (hasMore)
                        {
                            // Coroutine has more steps - leave it in the queue for next frame
                            // But break out of this item so we respect the yield boundary
                            // (don't advance again this frame)
                            break;
                        }
                        else
                        {
                            // Coroutine finished
                            idx++;
                            try
                            {
                                item.OnComplete?.Invoke();
                            }
                            catch (Exception ex)
                            {
                                FiresLogger.LogWarning($"[FrameBudgetScheduler] OnComplete failed: {ex.Message}");
                            }
                        }
                    }
                    else if (item.Action != null)
                    {
                        idx++;
                        try
                        {
                            item.Action.Invoke();
                        }
                        catch (Exception ex)
                        {
                            FiresLogger.LogWarning($"[FrameBudgetScheduler] Action failed: {ex.Message}");
                        }
                    }
                    else
                    {
                        // Empty item, skip
                        idx++;
                    }
                }

                // Clean up completed items in this lane
                if (idx >= list.Count)
                {
                    list.Clear();
                    idx = 0;
                }
            }

            _frameSw.Stop();
            _lastFrameMs = (float)_frameSw.Elapsed.TotalMilliseconds;
            _budgetExceeded = budgetExhausted;

            // Recount pending
            total = 0;
            for (int lane = 0; lane < LANE_COUNT; lane++)
            {
                total += _localQueues[lane].Count - _localIndex[lane];
            }
            _pendingCount = total;
        }

        #endregion
    }
}
