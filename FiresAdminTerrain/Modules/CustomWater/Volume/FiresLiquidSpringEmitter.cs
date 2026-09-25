using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Round-75 — INFINITE SOURCE for vanilla liquid pours.
    //
    // Vanilla LiquidVolume pours a fixed m_initialVolume once in Awake and
    // then only ever redistributes/drains it (grid border cells are zeroed
    // every sim tick — water reaching the 64m grid edge is deleted, which
    // reads as "flowed away downhill"). A spring needs continuous inflow:
    // this component injects depth into the grid-center cells at a fixed
    // rate, using the SAME mutex the background sim thread locks
    // (m_meshDataLock), then marks the sim dirty + needs-saving so the
    // mesh rebuilds and the owner's periodic Save syncs the added water
    // to everyone else.
    //
    // Ownership: LiquidVolume runs its sim on EVERY client independently
    // and reconciles via the owner's ZDO saves (s_liquidData). Injecting
    // on a non-owner would be overwritten by the next Load — so only the
    // ZDO owner emits.
    //
    // Persistence: PlaceSpring stamps ZDO float `va_spring_rate` at spawn.
    // The LiquidVolume.Awake postfix below re-attaches this component on
    // restored pours after world reload, so springs keep flowing forever.
    //
    // Fullness guard: when the center cell is within 90% of m_maxDepth the
    // emitter idles (bowl is full / contained) and resumes as water drains.
    public sealed class FiresLiquidSpringEmitter : MonoBehaviour
    {
        public const string ZDO_RATE_KEY = "va_spring_rate";

        // m³ per second poured into the source square. ~2 m³/s keeps a
        // visible stream running off a hillside without instantly maxing
        // a contained bowl.
        public float m_emitPerSecond = 2f;

        // Round-78 — anti-spill / bank-respect. A wet cell is KEPT only if
        // it is either deep enough to be a real body of water, OR contained
        // by a true bank (some neighbour's TERRAIN rises at least
        // BANK_MIN_RELIEF above this cell's water surface). Thin water that
        // has sheeted onto open/flat ground (no containing bank) is culled —
        // this is the "needs a certain cut depth before it overflows" logic
        // the user asked for, and it kills the z-fighting path spillage.
        public float m_shallowCull = 0.35f;   // m — thin water on flat ground is culled
        public float m_bankRelief = 0.5f;     // m — local relief needed to count as a cut/channel

        // Round-79 — the ruler fill level. The source pool LEVELS OUT here
        // (does not keep rising to overflow the cut). NaN = legacy pours
        // spawned before this field existed → fall back to the old
        // maxDepth-fraction guard so they still emit.
        public float m_fillY = float.NaN;

        private LiquidVolume _lv;
        private ZNetView _nview;
        private bool _loggedStart;
        private float _cullTimer;

        private void Awake()
        {
            _lv = GetComponentInChildren<LiquidVolume>(true);
            _nview = GetComponentInChildren<ZNetView>(true);
        }

        private void Update()
        {
            if (_lv == null || _nview == null || !_nview.IsValid()) return;
            if (!_nview.IsOwner()) return;

            // Throttle to ~5 Hz — the sim ticks at 20 Hz, we don't need to
            // fight it every frame, and this keeps the mutex contention low.
            _cullTimer += Time.deltaTime;
            if (_cullTimer < 0.2f) return;
            float dt = _cullTimer;
            _cullTimer = 0f;

            try
            {
                var depths = FiresLiquidGridOps.Depths(_lv);
                var heights = FiresLiquidGridOps.Heights(_lv);
                var mutex = FiresLiquidGridOps.MeshDataLock(_lv);
                if (depths == null || heights == null || mutex == null) return;
                if (heights.Count != depths.Count) return;

                int num = _lv.m_width + 1;
                int c = _lv.m_width / 2;
                int half = Mathf.Max(1, _lv.m_initialArea / 2);
                int srcHalf = half + 1;
                float baseY = _lv.transform.position.y;

                System.Threading.Monitor.Enter(mutex);
                try
                {
                    // ── Emit: TOP the source cells toward the ruler fill
                    // level and no higher. The pool levels out at fillY;
                    // the excess flows out through the sim and overflows only
                    // where the fillY surface clears the lowest bank. Rate-
                    // limited so it feeds a continuous stream, not a flood.
                    float budget = m_emitPerSecond * dt;              // total m³ this tick
                    int srcCells = (half * 2 + 1) * (half * 2 + 1);
                    float perCell = budget / srcCells;
                    for (int z = c - half; z <= c + half; z++)
                    {
                        for (int x = c - half; x <= c + half; x++)
                        {
                            int i = z * num + x;
                            float target = float.IsNaN(m_fillY)
                                ? _lv.m_maxDepth * 0.9f               // legacy fallback
                                : Mathf.Max(0f, m_fillY - heights[i] - baseY);
                            if (depths[i] < target)
                                depths[i] = Mathf.Min(target, depths[i] + perCell);
                        }
                    }

                    // ── Cull flat-ground spill EVERY tick (not gated on
                    // emit), so persisted spill is scrubbed too.
                    FiresLiquidGridOps.CullFlatSpill(depths, heights, num, c, c,
                        srcHalf, m_shallowCull, m_bankRelief);

                    FiresLiquidGridOps.Dirty(_lv) = true;
                    FiresLiquidGridOps.NeedsSaving(_lv) = true;
                }
                finally
                {
                    System.Threading.Monitor.Exit(mutex);
                }

                if (!_loggedStart)
                {
                    _loggedStart = true;
                    Debug.Log($"[FiresLiquidSpring] emitter live on '{name}' at {transform.position} — {m_emitPerSecond:F1} m³/s, level out at fillY={m_fillY:F1} (owner).");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[FiresLiquidSpring] emit failed: {ex.Message}");
                enabled = false;
            }
        }

        // Re-attach emitters on pours restored from ZDO after world load.
        [HarmonyPatch(typeof(LiquidVolume), "Awake")]
        private static class LiquidVolume_Awake_ReattachSpringEmitter
        {
            [HarmonyPostfix]
            private static void Postfix(LiquidVolume __instance)
            {
                try
                {
                    var root = __instance.transform.root.gameObject;
                    if (!root.name.StartsWith(FiresLiquidWaterPrefab.PrefabName)) return;
                    if (root.GetComponent<FiresLiquidSpringEmitter>() != null) return;

                    var nview = root.GetComponentInChildren<ZNetView>(true);
                    var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                    if (zdo == null) return;

                    float rate = zdo.GetFloat(ZDO_RATE_KEY, 0f);
                    if (rate <= 0f) return;

                    var emitter = root.AddComponent<FiresLiquidSpringEmitter>();
                    emitter.m_emitPerSecond = rate;
                    // Round-79 — restore the fill level so the pool still
                    // levels out at the ruler height after reload.
                    emitter.m_fillY = zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_FILL, float.NaN);
                    Debug.Log($"[FiresLiquidSpring] restored emitter on '{root.name}' at {root.transform.position} rate={rate:F1} m³/s fillY={emitter.m_fillY:F1}.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[FiresLiquidSpring] restore-attach failed: {ex.Message}");
                }
            }
        }
    }
}
