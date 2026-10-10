using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // Round-82 — FiresLiquidRiver. Replaces vanilla LiquidVolume's flat-settle
    // sim with a SOURCE-DISK + BANK-FOLLOWING sim so a spring makes a sloped
    // river instead of one flat pool. We keep the vanilla LiquidVolume whole —
    // its threaded mesh builder (the look), terrain raycast (m_heights),
    // persistence (s_liquidData) and swim (LiquidSurface) — and only swap out
    // the per-tick fluid update.
    //
    // A Harmony prefix on LiquidVolume.UpdateLiquid delegates to this
    // controller's SimStep and returns false, so vanilla's EvenDepth (which
    // would level our river flat) never runs for our pours. UpdateLiquid runs
    // on the volume's WORKER thread already holding m_meshDataLock, so SimStep
    // is thread-safe against the mesh builder — BUT it must never touch Unity
    // API (transform, GetComponent). Everything transform-derived (the source
    // mask, bank heights, base Y) is precomputed on the MAIN thread in Init().
    //
    // This is why the old FiresLiquidSpringEmitter failed: it edited m_depths
    // on the main thread while vanilla flattened it on the worker thread — a
    // fight our water always lost. Replacing UpdateLiquid removes the fight.
    public sealed class FiresLiquidRiverController : MonoBehaviour
    {
        // Set by FiresLiquidWaterPrefab.Spawn before/at activation.
        public float m_fillY;             // ruler source-pool level (world Y) — the PRIMARY source
        public float m_bankOffset = 1f;   // X — surface sits this far below the local bank
        public float m_sourceRadius;      // ruler radius (world m) — the PRIMARY source
        public Vector3 m_sourceCenter;    // ruler center (world) — the PRIMARY source
        public bool m_frozen;             // round-84 — sim frozen: mesh held, no flow/overflow
        public bool m_still;              // still water (lakes/ponds): dead-level cap at the source level, not bank-following
        private float _stillLevelRel;     // highest source fill, transform-relative (RecomputeSourceMask)

        // Round-85 — MULTI-SOURCE. A spring dropped inside an existing pour's
        // grid MERGES here as an extra source disk (one continuous mesh, no
        // overlapping-sheet seam / double-blend) rather than spawning a second
        // LiquidVolume. Each source pins its own disk to its own fill level;
        // where disks overlap the higher fill wins. Persisted as a ZDO blob so
        // merged rivers restore whole after reload.
        private struct RiverSource { public float x, z, radius, fillY; }
        private readonly List<RiverSource> _sources = new List<RiverSource>();
        private float[] _srcFillRel;   // per-cell source target fill (rel to baseY), max over covering disks

        // Local terrain-rim scan for the bank height (cells).
        private const int BANK_SCAN = 8;
        // Passes of the flow relaxation per sim tick.
        private const int FLOW_PASSES = 2;

        private LiquidVolume _lv;
        private bool _init;
        private bool[] _isSource;
        private bool[] _blocked;     // round-84 — DAM cells: never fill, no flow across (mop barrier)
        private float[] _bankRel;   // bank height RELATIVE to base Y (max m_heights in scan)
        private int _num;
        private bool _loggedInit;

        // Registry so the worker-thread prefix can resolve a volume's
        // controller WITHOUT a GetComponent call (illegal off-main-thread).
        private static readonly Dictionary<LiquidVolume, FiresLiquidRiverController> s_byVolume =
            new Dictionary<LiquidVolume, FiresLiquidRiverController>();
        private static readonly object s_lock = new object();

        public static bool TryGet(LiquidVolume lv, out FiresLiquidRiverController c)
        {
            lock (s_lock) { return s_byVolume.TryGetValue(lv, out c) && c != null; }
        }

        private void Awake()
        {
            _lv = GetComponent<LiquidVolume>() ?? GetComponentInChildren<LiquidVolume>(true);
            if (_lv != null) lock (s_lock) s_byVolume[_lv] = this;
        }

        private void OnDestroy()
        {
            if (_lv != null) lock (s_lock) s_byVolume.Remove(_lv);
        }

        // MAIN-THREAD init: once the volume's terrain raycast heights are
        // populated, precompute the source-cell mask and per-cell bank
        // heights. Transform access is legal here (main thread).
        private void Update()
        {
            if (_init) { FollowZdoFlags(); return; }
            if (_lv == null) return;
            if (!FiresLiquidGridOps.HaveHeights(_lv)) return;

            var heights = FiresLiquidGridOps.Heights(_lv);
            var mutex = FiresLiquidGridOps.MeshDataLock(_lv);
            if (heights == null || mutex == null) return;

            int num = _lv.m_width + 1;
            if (heights.Count != num * num) return;

            System.Threading.Monitor.Enter(mutex);
            try { Init(num, heights); }
            finally { System.Threading.Monitor.Exit(mutex); }
            _init = true;

            if (!_loggedInit)
            {
                _loggedInit = true;
                int src = 0; for (int i = 0; i < _isSource.Length; i++) if (_isSource[i]) src++;
                // Round-90 — second skin pass now that the world is fully up
                // (the ocean material is captured by now, so the texture copy
                // lands even if the Awake-time pass ran before capture).
                FiresLiquidWaterPrefab.ApplyFiresLiquidSkin(gameObject);
                FiresLogger.LogInfo($"[FiresLiquidRiver] init on '{name}' at {transform.position} | grid={num}x{num} sourceCells={src} fillY={m_fillY:F1} bankOffset={m_bankOffset:F1}");
            }
        }

        private void Init(int num, List<float> heights)
        {
            _num = num;
            _isSource = new bool[num * num];
            _blocked = new bool[num * num];
            _bankRel = new float[num * num];
            _srcFillRel = new float[num * num];

            // Sources: prefer the persisted blob (merged rivers survive reload),
            // else seed the primary source from the spawn fields (fresh spawn or
            // a pre-round-85 pour that predates the blob).
            if (_sources.Count == 0) LoadSources();
            if (_sources.Count == 0)
                _sources.Add(new RiverSource { x = m_sourceCenter.x, z = m_sourceCenter.z, radius = m_sourceRadius, fillY = m_fillY });

            LoadBlocked();
            var nv0 = Nview();
            if (nv0 != null && nv0.IsValid())
            {
                m_frozen = nv0.GetZDO().GetInt(FiresLiquidGridOps.ZDO_SPRING_FROZEN, m_frozen ? 1 : 0) != 0;
                m_still = nv0.GetZDO().GetInt(FiresLiquidGridOps.ZDO_SPRING_STILL, m_still ? 1 : 0) != 0;
            }

            // Bank = highest terrain within the scan (the containing rim),
            // in the same base-relative space as m_heights.
            for (int z = 0; z < num; z++)
            {
                for (int x = 0; x < num; x++)
                {
                    float bank = heights[z * num + x];
                    for (int dz = -BANK_SCAN; dz <= BANK_SCAN; dz++)
                    {
                        int nz = z + dz; if (nz < 0 || nz >= num) continue;
                        for (int dx = -BANK_SCAN; dx <= BANK_SCAN; dx++)
                        {
                            int nx = x + dx; if (nx < 0 || nx >= num) continue;
                            float t = heights[nz * num + nx];
                            if (t > bank) bank = t;
                        }
                    }
                    _bankRel[z * num + x] = bank;
                }
            }

            // HEAL + INVARIANT: a source obeys the SAME ceiling as every other
            // cell — the local bank cap. The first heal round capped at
            // bed+10m, which still left 8-10m pinned domes standing over the
            // settled water (user screenshots), and frozen pours never
            // redistributed their persisted blob depths at all. Now: fill ≤
            // bank − bankOffset at the source cell (never below bed+0.3 so a
            // spring still springs), and any heal UNFREEZES the sim so the
            // old blob drains through the normal cap path.
            {
                Vector3 p0 = transform.position;
                float half0 = _lv.m_width * _lv.m_scale * 0.5f;
                bool healed = false;
                for (int s = 0; s < _sources.Count; s++)
                {
                    var src = _sources[s];
                    int sx = Mathf.Clamp(Mathf.RoundToInt((src.x - (p0.x - half0)) / _lv.m_scale), 0, num - 1);
                    int sz = Mathf.Clamp(Mathf.RoundToInt((src.z - (p0.z - half0)) / _lv.m_scale), 0, num - 1);
                    int si = sz * num + sx;
                    float capRel = Mathf.Max(_bankRel[si] - m_bankOffset, heights[si] + 0.3f);
                    float maxFill = p0.y + capRel;
                    if (src.fillY > maxFill)
                    {
                        FiresLogger.LogWarning($"[FiresLiquidRiver] HEALED over-bank source on '{name}': fillY {src.fillY:F1} → {maxFill:F1} (bank cap).");
                        src.fillY = maxFill;
                        _sources[s] = src;
                        healed = true;
                    }
                }
                if (healed)
                {
                    int ci = (num / 2) * num + (num / 2);
                    float centerCap = p0.y + Mathf.Max(_bankRel[ci] - m_bankOffset, heights[ci] + 0.3f);
                    if (m_fillY > centerCap) m_fillY = centerCap;
                    SaveSources();
                    if (m_frozen) SetFrozen(false);   // let the persisted blob drain
                }
            }

            RecomputeSourceMask();
        }

        // MAIN-THREAD — rebuild the source-cell mask + per-cell source fill from
        // the current source list. Transform access is legal here; callers that
        // touch the live grid hold the mesh lock. Cheap (only runs at init and
        // when a spring merges), so the full O(sources·cells) rescan is fine.
        private void RecomputeSourceMask()
        {
            if (_isSource == null || _lv == null) return;
            int num = _num;
            float half = _lv.m_width * _lv.m_scale * 0.5f;
            Vector3 p = transform.position;

            for (int i = 0; i < _isSource.Length; i++) { _isSource[i] = false; _srcFillRel[i] = float.MinValue; }

            _stillLevelRel = float.MinValue;
            for (int s = 0; s < _sources.Count; s++)
            {
                var src = _sources[s];
                float r2 = src.radius * src.radius;
                float fillRel = src.fillY - p.y;
                if (fillRel > _stillLevelRel) _stillLevelRel = fillRel;
                for (int z = 0; z < num; z++)
                {
                    float wz = p.z - half + z * _lv.m_scale;
                    float dz = wz - src.z;
                    for (int x = 0; x < num; x++)
                    {
                        float wx = p.x - half + x * _lv.m_scale;
                        float dx = wx - src.x;
                        if (dx * dx + dz * dz > r2) continue;
                        int i = z * num + x;
                        _isSource[i] = true;
                        if (fillRel > _srcFillRel[i]) _srcFillRel[i] = fillRel;
                    }
                }
            }
        }

        // Any source disk (primary or merged) within range of pos? The pour's
        // TRANSFORM stays at the first source, so proximity checks against
        // pour centers alone go blind to sources merged 60m downstream —
        // relay placement asks here instead.
        public bool HasSourceNear(Vector3 pos, float range)
        {
            float r2 = range * range;
            for (int i = 0; i < _sources.Count; i++)
            {
                float dx = pos.x - _sources[i].x, dz = pos.z - _sources[i].z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            float ddx = pos.x - m_sourceCenter.x, ddz = pos.z - m_sourceCenter.z;
            return ddx * ddx + ddz * ddz <= r2;
        }

        // MAIN-THREAD — persist the primary source once at spawn so a merged
        // river restores whole. Idempotent (no-op if the list is already seeded).
        public void SeedPrimarySource()
        {
            if (_sources.Count > 0) return;
            _sources.Add(new RiverSource { x = m_sourceCenter.x, z = m_sourceCenter.z, radius = m_sourceRadius, fillY = m_fillY });
            SaveSources();
        }

        // MAIN-THREAD — merge a new spring into THIS pour as an extra source
        // disk (one continuous mesh). Recomputes the source mask under the mesh
        // lock and unfreezes the sim so the added water flows. Persisted.
        public void AddSource(Vector3 center, float radius, float fillY)
        {
            if (_lv == null) return;
            _sources.Add(new RiverSource { x = center.x, z = center.z, radius = radius, fillY = fillY });
            SaveSources();
            if (m_frozen) SetFrozen(false);

            if (_init)
            {
                var mutex = FiresLiquidGridOps.MeshDataLock(_lv);
                if (mutex != null)
                {
                    System.Threading.Monitor.Enter(mutex);
                    try
                    {
                        RecomputeSourceMask();
                        FiresLiquidGridOps.Dirty(_lv) = true;
                        FiresLiquidGridOps.NeedsSaving(_lv) = true;
                    }
                    finally { System.Threading.Monitor.Exit(mutex); }
                }
                else RecomputeSourceMask();
            }
            FiresLogger.LogInfo($"[FiresLiquidRiver] merged source #{_sources.Count} at {center} r={radius:F1} fillY={fillY:F1} into '{name}'.");
        }

        private void SaveSources()
        {
            var nv = Nview();
            if (nv == null || !nv.IsValid()) return;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(_sources.Count);
                for (int i = 0; i < _sources.Count; i++)
                {
                    var s = _sources[i];
                    bw.Write(s.x); bw.Write(s.z); bw.Write(s.radius); bw.Write(s.fillY);
                }
                nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_SOURCES, Utils.Compress(ms.ToArray()));
            }
        }

        private void LoadSources()
        {
            var nv = Nview();
            if (nv == null || !nv.IsValid()) return;
            var packed = nv.GetZDO().GetByteArray(FiresLiquidGridOps.ZDO_SPRING_SOURCES);
            if (packed == null) return;
            try
            {
                var bytes = Utils.Decompress(packed);
                using (var ms = new MemoryStream(bytes))
                using (var br = new BinaryReader(ms))
                {
                    int n = br.ReadInt32();
                    _sources.Clear();
                    for (int i = 0; i < n; i++)
                        _sources.Add(new RiverSource
                        {
                            x = br.ReadSingle(), z = br.ReadSingle(),
                            radius = br.ReadSingle(), fillY = br.ReadSingle()
                        });
                }
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresLiquidRiver] LoadSources: {ex.Message}"); }
        }

        private ZNetView Nview() => GetComponentInChildren<ZNetView>(true);

        // 0.2.740: the freeze and still toggles are written to the ZDO by the peer that ran them (it claims ownership first), but
        // every peer read them only once, at init, so a spring frozen on one client kept flowing on the others until a reload
        // (Run A 10-05, cave/water_freeze: this client SpringFrozen 1, the bot 0). A peer that does not own the spring now follows
        // its ZDO, checked twice a second.
        private const float FlagFollowSeconds = 0.5f;
        private float _nextFlagFollow;

        private void FollowZdoFlags()
        {
            if (Time.time < _nextFlagFollow) return;
            _nextFlagFollow = Time.time + FlagFollowSeconds;
            var nv = Nview();
            if (nv == null || !nv.IsValid() || nv.IsOwner()) return;
            var zdo = nv.GetZDO();
            bool frozen = zdo.GetInt(FiresLiquidGridOps.ZDO_SPRING_FROZEN, m_frozen ? 1 : 0) != 0;
            bool still = zdo.GetInt(FiresLiquidGridOps.ZDO_SPRING_STILL, m_still ? 1 : 0) != 0;
            if (frozen != m_frozen)
            {
                m_frozen = frozen;
                FiresLogger.LogDebug($"[FiresLiquidRiver] '{name}' sim {(frozen ? "FROZEN" : "UNFROZEN")} by its owner (followed from the ZDO).");
            }
            if (still != m_still)
            {
                m_still = still;
                RecomputeSourceMask();
                FiresLogger.LogDebug($"[FiresLiquidRiver] '{name}' {(still ? "still water" : "flowing water")} by its owner (followed from the ZDO).");
            }
        }

        // STILL WATER (lakes/ponds): the cap becomes a dead-level line at the
        // highest source level instead of bank-following — the surface levels
        // out exactly like standing water should, and equal-level merged
        // basins read as one sheet. Rivers keep the sloped bank-following
        // cap. Persisted.
        public void SetStillWater(bool still)
        {
            m_still = still;
            var nv = Nview();
            if (nv != null && nv.IsValid())
                nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_STILL, still ? 1 : 0);
            if (_init) RecomputeSourceMask();
            if (m_frozen) SetFrozen(false);
        }

        // Round-84 — freeze/unfreeze the sim. Frozen = the mesh holds its
        // current shape and no flow/overflow runs (SimStep no-ops). Persisted.
        private static readonly System.Reflection.MethodInfo s_lvSave =
            AccessTools.Method(typeof(LiquidVolume), "Save");

        // FREEZING MEANS THE SHAPE IS FINAL, SO PERSIST IT NOW.
        //
        // Vanilla only writes m_depths to the ZDO from CheckSave, and
        // m_needsSaving is set in exactly one place: EvenDepth, inside
        // UpdateLiquid (LiquidVolume:353). Freezing makes our prefix skip
        // UpdateLiquid entirely — so nothing ever marks the volume dirty again
        // and the depth grid is NEVER saved.
        //
        // On reload HaveSavedData() then finds no s_liquidData, Awake falls
        // through to InitializeLevels(), and that pours m_initialVolume over a
        // few cells at the GRID CENTRE. That is the whole "came back drained"
        // bug: a frozen river restores as a puddle in the middle of its own
        // channel. It hits hand-frozen springs and auto-poured ones alike, and
        // the tighter the freeze follows the pour the more certain it is —
        // m_saveInterval is 4s, so a freeze a few frames after spawning never
        // had a save to miss.
        private void ForceSave()
        {
            if (_lv == null) return;
            try
            {
                var nv = Nview();
                if (nv != null && nv.IsValid() && !nv.IsOwner()) nv.ClaimOwnership();
                FiresLiquidGridOps.NeedsSaving(_lv) = true;
                s_lvSave?.Invoke(_lv, null);
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresLiquidRiver] force-save on freeze failed: {ex.Message}");
            }
        }

        public void SetFrozen(bool frozen)
        {
            m_frozen = frozen;
            var nv = Nview();
            if (nv != null && nv.IsValid())
                nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_FROZEN, frozen ? 1 : 0);
            if (frozen) ForceSave();
            FiresLogger.LogInfo($"[FiresLiquidRiver] '{name}' sim {(frozen ? "FROZEN (mesh held)" : "UNFROZEN (flowing)")}.");
        }

        // MAIN-THREAD — mark grid cells within `radius` of `centerXZ` as a DAM.
        // Blocked cells never fill and water can't flow across them, so mopping
        // an edge stops the sim re-flowing over it — manual control of the mesh
        // after the sim draws it. Persisted (ZDO bitmask) so the dam survives
        // reload. Returns cells blocked.
        public int BlockCells(Vector2 centerXZ, float radius)
        {
            if (!_init || _lv == null || _blocked == null) return 0;
            var depths = FiresLiquidGridOps.Depths(_lv);
            var mutex = FiresLiquidGridOps.MeshDataLock(_lv);
            if (depths == null || mutex == null) return 0;

            int num = _num;
            float half = _lv.m_width * _lv.m_scale * 0.5f;
            Vector3 p = transform.position;
            float r2 = radius * radius;
            int count = 0;

            System.Threading.Monitor.Enter(mutex);
            try
            {
                for (int z = 0; z < num; z++)
                    for (int x = 0; x < num; x++)
                    {
                        float wx = p.x - half + x * _lv.m_scale;
                        float wz = p.z - half + z * _lv.m_scale;
                        float dx = wx - centerXZ.x, dz = wz - centerXZ.y;
                        if (dx * dx + dz * dz > r2) continue;
                        int i = z * num + x;
                        if (!_blocked[i]) { _blocked[i] = true; count++; }
                        depths[i] = 0f;
                    }
                FiresLiquidGridOps.Dirty(_lv) = true;
                FiresLiquidGridOps.NeedsSaving(_lv) = true;
            }
            finally { System.Threading.Monitor.Exit(mutex); }

            if (count > 0) SaveBlocked();
            FiresLogger.LogInfo($"[FiresLiquidRiver] dam +{count} cells (r={radius:F1}) on '{name}'.");
            return count;
        }

        private void SaveBlocked()
        {
            var nv = Nview();
            if (nv == null || !nv.IsValid() || _blocked == null) return;
            int n = _blocked.Length;
            var bytes = new byte[(n + 7) / 8];
            for (int i = 0; i < n; i++) if (_blocked[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
            nv.GetZDO().Set(FiresLiquidGridOps.ZDO_SPRING_BLOCKED, Utils.Compress(bytes));
        }

        private void LoadBlocked()
        {
            var nv = Nview();
            if (nv == null || !nv.IsValid() || _blocked == null) return;
            var packed = nv.GetZDO().GetByteArray(FiresLiquidGridOps.ZDO_SPRING_BLOCKED);
            if (packed == null) return;
            try
            {
                var bytes = Utils.Decompress(packed);
                int n = Mathf.Min(_blocked.Length, bytes.Length * 8);
                for (int i = 0; i < n; i++) _blocked[i] = (bytes[i >> 3] & (1 << (i & 7))) != 0;
            }
            catch (System.Exception ex) { FiresLogger.LogWarning($"[FiresLiquidRiver] LoadBlocked: {ex.Message}"); }
        }

        // WORKER-THREAD — the infinite source: pin every source cell to its
        // fill level, then vanilla UpdateLiquid (which runs right after this)
        // settles the water exactly like the Water Volume tool. Blocked (dam)
        // cells stay dry.
        public void PinSources()
        {
            if (!_init) return;
            var depths = FiresLiquidGridOps.Depths(_lv);
            var heights = FiresLiquidGridOps.Heights(_lv);
            if (depths == null || heights == null) return;
            int num = _num;
            if (depths.Count != num * num || heights.Count != num * num) return;
            bool moved = false;
            for (int i = 0; i < _isSource.Length; i++)
            {
                if (_blocked[i]) { if (depths[i] != 0f) { depths[i] = 0f; moved = true; } continue; }
                if (!_isSource[i]) continue;
                float want = Mathf.Max(0f, _srcFillRel[i] - heights[i]);
                if (depths[i] < want - 1e-4f) { depths[i] = want; moved = true; }
            }
            if (moved)
            {
                FiresLiquidGridOps.Dirty(_lv) = true;
                FiresLiquidGridOps.NeedsSaving(_lv) = true;
            }
        }

        // Legacy custom sim (superseded by PinSources + vanilla settle; kept
        // for the console still/dam paths that reference its helpers).
        public bool SimStep(float dt)
        {
            if (!_init) return false;
            if (m_frozen) return false;   // round-84 — sim frozen: hold the mesh, no flow

            var depths = FiresLiquidGridOps.Depths(_lv);
            var heights = FiresLiquidGridOps.Heights(_lv);
            if (depths == null || heights == null) return false;
            int num = _num;
            if (depths.Count != num * num || heights.Count != num * num) return false;
            if (_bankRel.Length != num * num) return false;

            bool moved = false;
            float visc = Mathf.Max(0.01f, _lv.m_viscocity);
            float maxD = dt * visc;

            // 1) Pin each source disk to ITS ruler fill level (infinite source).
            //    _srcFillRel holds the per-cell target (max over overlapping
            //    disks) so merged springs each hold their own level.
            for (int i = 0; i < _isSource.Length; i++)
            {
                if (!_isSource[i]) continue;
                if (_blocked[i]) { if (depths[i] != 0f) { depths[i] = 0f; moved = true; } continue; }
                float want = Mathf.Max(0f, _srcFillRel[i] - heights[i]);
                if (Mathf.Abs(depths[i] - want) > 1e-4f) { depths[i] = want; moved = true; }
            }

            // 2) Flow relaxation — water moves toward lower SURFACE neighbours
            //    (SettlePair below: pairwise settling). More water goes to the steeper
            //    drop, so the stream heads out the open/downhill side. Water
            //    can NOT flow across a blocked (dam) cell.
            for (int pass = 0; pass < FLOW_PASSES; pass++)
            {
                for (int z = 0; z < num - 1; z++)
                    for (int x = 0; x < num - 1; x++)
                        moved |= SettlePair(depths, heights, _blocked, z * num + x, z * num + x + 1, visc);
                for (int x = 0; x < num - 1; x++)
                    for (int z = 0; z < num - 1; z++)
                        moved |= SettlePair(depths, heights, _blocked, z * num + x, (z + 1) * num + x, visc);
            }

            // 3) Bank cap — no non-source cell may rise above (bank − X). On
            //    flat ground bank≈terrain so the cap is 0 → the cell drains
            //    (no sheet on the flats). Because nothing can pile above the
            //    cap, the continuously-fed source spreads ONWARD and downhill,
            //    filling each cell to bank−X: the surface follows the slope.
            //    Blocked (dam) cells are forced dry.
            for (int i = 0; i < num * num; i++)
            {
                if (_blocked[i]) { if (depths[i] != 0f) { depths[i] = 0f; moved = true; } continue; }
                if (_isSource[i]) continue;
                // Still water (lakes/ponds): the cap is a DEAD-LEVEL line at
                // the highest source level — standing water levels out
                // instead of terracing over every stump/hump the way the
                // bank-following river cap does. Rivers keep the slope.
                float cap = m_still
                    ? Mathf.Max(0f, _stillLevelRel - heights[i])
                    : Mathf.Max(0f, _bankRel[i] - m_bankOffset - heights[i]);
                if (depths[i] > cap) { depths[i] = cap; moved = true; }
            }

            // 4) Drain the grid borders (vanilla behaviour) so water reaching
            //    the edge leaves instead of piling.
            for (int i = 0; i < num; i++)
            {
                depths[i] = 0f;
                depths[(num - 1) * num + i] = 0f;
                depths[i * num] = 0f;
                depths[i * num + (num - 1)] = 0f;
            }

            if (moved)
            {
                FiresLiquidGridOps.Dirty(_lv) = true;
                FiresLiquidGridOps.NeedsSaving(_lv) = true;
            }
            return moved;
        }

        // Two neighbouring cells settle toward one water surface: the cell standing higher passes water to the lower one.
        // How much moves in one step: the square root of (surface gap x viscosity), never more than half the gap (so the
        // pair cannot overshoot and swap sides) and never more water than the giver holds. A dam cell on either side, or
        // surfaces already within a millimetre, moves nothing. True when water moved.
        private const float SettledGap = 0.001f;

        private static bool SettlePair(List<float> depths, List<float> heights, bool[] blocked, int i0, int i1, float visc)
        {
            if (blocked[i0] || blocked[i1]) return false;
            float depthA = depths[i0], depthB = depths[i1];
            if (depthA <= 0f && depthB <= 0f) return false;
            float surfaceA = heights[i0] + depthA, surfaceB = heights[i1] + depthB;
            if (Mathf.Abs(surfaceB - surfaceA) < SettledGap) return false;

            bool aGives = surfaceA > surfaceB;
            int giver = aGives ? i0 : i1, taker = aGives ? i1 : i0;
            float held = aGives ? depthA : depthB;
            if (held <= 0f) return false;
            float gap = aGives ? surfaceA - surfaceB : surfaceB - surfaceA;
            float flow = Mathf.Min(Mathf.Min(Mathf.Pow(gap * visc, 0.5f), gap * 0.5f), held);
            float giverAfter = held - flow;
            float takerAfter = (aGives ? depthB : depthA) + flow;
            depths[giver] = Mathf.Max(0f, giverAfter);
            depths[taker] = Mathf.Max(0f, takerAfter);
            return true;
        }

        // ── Harmony: replace the vanilla per-tick fluid update for our pours ──
        [HarmonyPatch(typeof(LiquidVolume), "UpdateLiquid")]
        private static class LiquidVolume_UpdateLiquid_Replace
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(LiquidVolume __instance, float dt)
            {
                if (!TryGet(__instance, out var c)) return true;   // vanilla tar / Water Volume — untouched
                // THE SPRING IS A WATER VOLUME WITH AN INFINITE SOURCE (the
                // original design, restored). The custom SimStep replacement
                // — source pin + bank-following caps + border drains — kept
                // producing domes and terraces while the Water Volume tool's
                // untouched VANILLA settle behaved exactly right ("settles
                // over terrain, drapes edges, pools in depressions"). So:
                // pin the source cells to their fill levels, then let the
                // REAL vanilla UpdateLiquid run the settle.
                if (c.m_frozen) return false;                       // frozen: hold the mesh
                // 0.2.752 (Fire 10-05: "we've ended up with these massive water pours everywhere ... we have to stop it, rivers
                // were meant to be laid out purposefully"): a spring keeps its own circle full at its level and NOTHING spreads
                // it. The vanilla settle used to carry every pinned top-up downhill across the pour's whole grid, every
                // session, so each spring grew without end. Water already spread before this stays as it was; none is added.
                c.PinSources();
                return false;
            }
        }

        // ── Re-attach the controller on pours restored from ZDO after a
        //    world/zone reload, from FAT's one LiquidVolume.Awake hook
        //    (FatVanillaHooks). Reads the spring params stamped at spawn.
        internal static void ReattachRiver(LiquidVolume volume)
        {
            try
            {
                var root = volume.transform.root.gameObject;
                if (!root.name.StartsWith(FiresLiquidWaterPrefab.PrefabName)) return;
                if (root.GetComponent<FiresLiquidRiverController>() != null) return;

                var nview = root.GetComponentInChildren<ZNetView>(true);
                var zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
                if (zdo == null) return;
                if (zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_RATE, 0f) <= 0f) return;

                var c = root.AddComponent<FiresLiquidRiverController>();
                c.m_fillY = zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_FILL, root.transform.position.y);
                c.m_bankOffset = zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_BANKOFFSET, 1f);
                c.m_sourceRadius = zdo.GetFloat(FiresLiquidGridOps.ZDO_SPRING_RADIUS, 3f);
                var p = root.transform.position;
                c.m_sourceCenter = new Vector3(p.x, c.m_fillY, p.z);
                // Round-90 — the restored instance wears the TEMPLATE's
                // vanilla water_liquid; re-apply our FiresLiquid skin.
                FiresLiquidWaterPrefab.ApplyFiresLiquidSkin(root);
                FiresLogger.LogInfo($"[FiresLiquidRiver] restored controller on '{root.name}' at {p} fillY={c.m_fillY:F1} bankOffset={c.m_bankOffset:F1} r={c.m_sourceRadius:F1}");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[FiresLiquidRiver] restore-attach failed: {ex.Message}");
            }
        }
    }
}
