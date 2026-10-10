using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace VerdantsAscent.Water.Core
{
    // ── Water VOLUME placement — settle + gel ────────────────────────────
    //
    // Second placement form alongside the drawn/floating water brush
    // (see Docs/PLAN_WaterVolumePlacement.md). One click pours a volume
    // of water defined by the IH ruler — radius = ruler circle, fill
    // height = ruler Y. The water then SETTLES into the terrain
    // depression over ~1.5 seconds (a conserved-volume flow relaxation,
    // rebuilt to the mesh every couple frames so the pour is visible),
    // and finally GELS: viscosity ramps to zero, the surface freezes in
    // place, and the result persists exactly like any other combiner
    // grid (the anchor/cache already store the surface heightfield).
    //
    // Vanilla inspiration: spawning liquid water and freezing it gives
    // shaped swimmable water. This is our controlled version of that.
    //
    // Partial on WaterMeshCombiner so the sim has direct access to the
    // private grid state (_surfaceHeights / _terrainHeights / _waterDepths
    // / SetupGrid / BuildWaterMesh) without widening the public API.
    public partial class WaterMeshCombiner
    {
        // How long the pour/settle animation runs before the gel locks
        // the shape. The viscosity ramp below eases flow to zero across
        // this window, so on a slope SOME water escapes downhill but a
        // pool always survives (user spec: must not completely run off).
        private const float VOLUME_SETTLE_DURATION = 1.5f;

        // Extra grid margin around the ruler circle so settling water can
        // spread outward into nearby depressions instead of hitting an
        // invisible wall at the disk edge.
        private const float VOLUME_SETTLE_MARGIN = 8f;

        // Relaxation passes per rendered frame. More passes = faster
        // convergence to the pooled shape within the settle window.
        private const int VOLUME_SIM_PASSES_PER_FRAME = 3;

        // Fraction of the surface-height difference that flows to lower
        // neighbours per pass at full viscosity. Kept conservative for
        // stability (each cell also caps total outflow at half its column
        // per pass).
        private const float VOLUME_FLOW_RATE = 0.35f;

        // Columns thinner than this render as "no water" (sentinel -100
        // in the surface grid — the mesh builder treats <= -50 as empty).
        private const float VOLUME_MIN_COLUMN = 0.02f;

        // Rebuild the visible mesh every N frames during the settle.
        // 2 ≈ 30fps animation at 60fps play — smooth enough, half the
        // mesh-build cost.
        private const int VOLUME_REBUILD_EVERY_N_FRAMES = 2;

        // ── Gel state ────────────────────────────────────────────────────
        //
        // _isGelled: the combiner's surface grid is AUTHORITATIVE data —
        // never recomputed from the disk list. RegenerateMesh routes to
        // RebuildFromStoredGrid instead of the IDW pipeline, and the
        // disk-footprint cull in BuildWaterMesh is bypassed (settled
        // water legitimately spreads outside the original ruler circle).
        // Persisted via the anchor ZDO (va_water_is_gelled) + cache v2.
        //
        // _isSettling: the pour sim is mid-flight; regeneration is
        // suppressed entirely (the coroutine owns the grid + mesh).
        private bool _isGelled;
        private bool _isSettling;

        public bool IsGelled
        {
            get => _isGelled;
            set => _isGelled = value;
        }

        public bool IsSettling => _isSettling;

        // Create a dedicated settling-volume combiner. Never absorbed
        // into an existing combiner (GetOrCreateCombinerFor skips gelled
        // and settling combiners for the same reason: their grids are
        // data, not derived state).
        //
        // center.y IS the fill level (the IH ruler Y). The bookkeeping
        // disk keeps delete/inspect tools working; the surface itself
        // comes from the sim.
        public static WaterMeshCombiner CreateSettlingVolume(Vector3 center, float radius, CustomLiquidType liquid)
        {
            var disk = new WaterDisk(center, radius, maxDepth: 4f, liquid);

            var go = new GameObject("WaterMeshCombiner_Volume");
            go.transform.position = disk.Position;

            var combiner = go.AddComponent<WaterMeshCombiner>();
            combiner.LiquidType = disk.LiquidType;
            combiner.CustomLiquidType = liquid;

            // Direct list add — NOT AddWaterDiskInternal, which would
            // MarkDirty and race the regen timer against our coroutine.
            combiner._waterDisks.Add(disk);
            combiner._isSettling = true;
            combiner._needsRegeneration = false;

            s_allCombiners.Add(combiner);
            combiner.SetupDamageHandler();

            combiner.StartCoroutine(combiner.SettleVolumeCoroutine(center, radius));

            FiresLogger.LogInfo($"[WaterMeshCombiner] CreateSettlingVolume at {center}, r={radius:F1}, fillY={center.y:F1}, liquid={liquid}");
            return combiner;
        }

        private IEnumerator SettleVolumeCoroutine(Vector3 center, float radius)
        {
            // Bounds = disk footprint + settle margin, then grid + terrain.
            CalculateCombinedBounds();
            _combinedBounds.Expand(new Vector3(VOLUME_SETTLE_MARGIN * 2f, 0f, VOLUME_SETTLE_MARGIN * 2f));
            SetupGrid();
            // Round-67: fill-anchored PHYSICS probe (sees custom tiles,
            // rocks, pieces — GetGroundHeight is heightmap-blind).
            _groundProbeFillY = center.y;
            yield return SampleRealGroundAsync();

            int w = _gridWidth, h = _gridHeight;
            float fillY = center.y;
            float[] column = new float[w * h];
            float[] next = new float[w * h];

            // Initial pour: inside the ruler circle, a column from terrain
            // up to the fill level. Terrain above the fill level → dry.
            // Round-68 — remember the pour cells: the settle loop re-tops
            // them to the fill level every frame until gel. Without this
            // the sim CONSERVES the poured volume, so as water spreads
            // outward the level drains below the ruler — the user asked
            // for "fills to the ring, settles into the depression, gels":
            // the circle is a maintained source until everything sets.
            float r2 = radius * radius;
            var pourCells = new System.Collections.Generic.List<int>(256);
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    Vector3 wp = GridToWorld(x, z);
                    float dx = wp.x - center.x;
                    float dz = wp.z - center.z;
                    if (dx * dx + dz * dz <= r2 && _terrainHeights[i] < fillY)
                    {
                        column[i] = fillY - _terrainHeights[i];
                        pourCells.Add(i);
                    }
                }
            }

            WriteColumnsToGrid(column);
            BuildWaterMesh();
            SetupMaterial();
            LogVolumeDiagnostics("volume pour START", column, fillY);

            // ── Settle loop ──────────────────────────────────────────────
            float t = 0f;
            int frame = 0;
            while (t < VOLUME_SETTLE_DURATION)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / VOLUME_SETTLE_DURATION);
                // Ease-out viscosity ramp: pours fast at first, thickens,
                // sets. This IS the gel — at 0 nothing moves any more.
                float viscosity = 1f - k * k;

                for (int pass = 0; pass < VOLUME_SIM_PASSES_PER_FRAME; pass++)
                    FlowPass(column, next, w, h, viscosity);

                // Round-68 — hold the fill level inside the pour circle
                // until gel (see pourCells note above). Outflow into the
                // surrounding depression keeps happening; the circle
                // itself never drops below the ruler level.
                for (int p = 0; p < pourCells.Count; p++)
                {
                    int i = pourCells[p];
                    float full = fillY - _terrainHeights[i];
                    if (column[i] < full) column[i] = full;
                }

                frame++;
                if (frame % VOLUME_REBUILD_EVERY_N_FRAMES == 0)
                {
                    WriteColumnsToGrid(column);
                    BuildWaterMesh();
                }

                yield return null;
            }

            // ── Gel ──────────────────────────────────────────────────────
            WriteColumnsToGrid(column);
            _isSettling = false;
            _isGelled = true;

            BuildWaterMesh();
            SetupMaterial();
            SetupSwimTrigger();
            DetectCliffEdges();
            SpawnAmbientVFX();

            MarkAllRowsDirty();
            EnsureAnchorExists();
            SaveToAnchor();

            LogVolumeDiagnostics("volume GELLED", column, fillY);
        }

        // Round-59 — placement diagnostics. The user reported swimmable-
        // but-INVISIBLE water: swim triggers read _surfaceHeights (box
        // colliders — working), while the visual comes from _waterMesh +
        // _meshRenderer. This dump tells us in one test run which link
        // in that chain is broken (empty mesh? disabled renderer? missing
        // material? culled bounds?).
        private void LogVolumeDiagnostics(string stage, float[] column, float fillY)
        {
            try
            {
                int wet = 0;
                float totalVol = 0f;
                float minT = float.MaxValue, maxT = float.MinValue;
                for (int i = 0; i < column.Length; i++)
                {
                    if (column[i] >= VOLUME_MIN_COLUMN) { wet++; totalVol += column[i]; }
                    if (_terrainHeights != null && i < _terrainHeights.Length)
                    {
                        if (_terrainHeights[i] < minT) minT = _terrainHeights[i];
                        if (_terrainHeights[i] > maxT) maxT = _terrainHeights[i];
                    }
                }

                int meshVerts = _waterMesh != null ? _waterMesh.vertexCount : -1;
                int meshTris = _waterMesh != null ? _waterMesh.triangles.Length / 3 : -1;
                // Round-65 — isVisible is Unity's own frustum/cull verdict
                // (false = the camera never draws this renderer at all);
                // renderQueue catches a wrapped material stuck at an opaque
                // queue position (terrain then paints over the ZWrite-Off
                // water everywhere = invisible-but-swimmable).
                string rendState = _meshRenderer == null ? "NULL"
                    : (_meshRenderer.enabled ? $"enabled isVisible={_meshRenderer.isVisible}" : "DISABLED");
                string matName = _meshRenderer != null && _meshRenderer.sharedMaterial != null
                    ? $"{_meshRenderer.sharedMaterial.name} ({_meshRenderer.sharedMaterial.shader?.name}) queue={_meshRenderer.sharedMaterial.renderQueue}"
                    : "NULL";
                string filterState = _meshFilter == null ? "NULL"
                    : (_meshFilter.sharedMesh == _waterMesh ? "bound" : "UNBOUND");

                FiresLogger.LogInfo($"[WaterVolumeSettle] {stage} | grid={_gridWidth}x{_gridHeight} cell={_cellSize:F2} " +
                    $"| wet={wet} vol={totalVol:F1}m³ fillY={fillY:F1} terrain=[{minT:F1}..{maxT:F1}] " +
                    $"| mesh verts={meshVerts} tris={meshTris} bounds={_waterMesh?.bounds.size} " +
                    $"| filter={filterState} renderer={rendState} mat={matName} layer={gameObject.layer} pos={transform.position}");

                // Round-72 — the WORLD-Y ledger. Ruler said fillY; these
                // lines say where every stage of the chain actually put
                // the water at the ring center, and WHICH collider the
                // ground probe believed. If the mesh is visibly below the
                // ring, exactly one of these numbers disagrees — that
                // number is the bug.
                int cx = Mathf.Clamp(Mathf.RoundToInt((transform.position.x - _gridOrigin.x) / _cellSize), 0, _gridWidth - 1);
                int cz = Mathf.Clamp(Mathf.RoundToInt((transform.position.z - _gridOrigin.z) / _cellSize), 0, _gridHeight - 1);
                int ci = cz * _gridWidth + cx;
                float terC = _terrainHeights != null && ci < _terrainHeights.Length ? _terrainHeights[ci] : float.NaN;
                float colC = column != null && ci < column.Length ? column[ci] : float.NaN;
                float surC = _surfaceHeights != null && ci < _surfaceHeights.Length ? _surfaceHeights[ci] : float.NaN;
                string meshWorldY = "n/a";
                if (_waterMesh != null && meshVerts > 0)
                {
                    var b = _waterMesh.bounds;
                    meshWorldY = $"[{transform.position.y + b.min.y:F1}..{transform.position.y + b.max.y:F1}]";
                }
                FiresLogger.LogInfo($"[WaterVolumeSettle] {stage} CENTER | terrain@center={terC:F2} column@center={colC:F2} " +
                    $"surfaceWorldY@center={surC:F2} (ruler fillY={fillY:F2}, diff={surC - fillY:+0.00;-0.00}) " +
                    $"| meshWorldY={meshWorldY} transformY={transform.position.y:F2}");
                FiresLogger.LogInfo($"[WaterVolumeSettle] {stage} PROBE | {ProbeLedgerAt(transform.position.x, transform.position.z)} " +
                    $"|| +4x: {ProbeLedgerAt(transform.position.x + 4f, transform.position.z)} " +
                    $"|| +4z: {ProbeLedgerAt(transform.position.x, transform.position.z + 4f)}");
            }
            catch (System.Exception ex)
            {
                FiresLogger.LogWarning($"[WaterVolumeSettle] diagnostics threw: {ex.Message}");
            }
        }

        // Round-72 — one probe column, spelled out: every collider the
        // ground ray crosses (name/layer/worldY, top-down) and which Y
        // the picker actually chose for the sim. This is the line that
        // says "the probe used the heightmap 2m under your custom tile"
        // or "the probe hit an old gelled water collider" in plain text.
        private string ProbeLedgerAt(float x, float z)
        {
            try
            {
                if (s_groundProbeMask < 0)
                    s_groundProbeMask = LayerMask.GetMask(
                        "Default", "static_solid", "piece", "terrain", "Default_small");

                float fill = float.IsNaN(_groundProbeFillY) ? transform.position.y : _groundProbeFillY;
                var origin = new Vector3(x, fill + GROUND_PROBE_MARGIN, z);
                int n = Physics.RaycastNonAlloc(origin, Vector3.down, s_groundProbeHits,
                    GROUND_PROBE_MARGIN + GROUND_PROBE_DEPTH, s_groundProbeMask,
                    QueryTriggerInteraction.Ignore);

                var hits = new List<RaycastHit>(n);
                for (int i = 0; i < n; i++) hits.Add(s_groundProbeHits[i]);
                hits.Sort((a, b) => b.point.y.CompareTo(a.point.y));

                var sb = new System.Text.StringBuilder(128);
                sb.Append($"({x:F0},{z:F0}) fill={fill:F1} hits={n}: ");
                for (int i = 0; i < hits.Count; i++)
                {
                    var hit = hits[i];
                    string cname = hit.collider != null ? hit.collider.name : "?";
                    string lname = hit.collider != null ? LayerMask.LayerToName(hit.collider.gameObject.layer) : "?";
                    sb.Append($"{hit.point.y:F1}({cname}/{lname}) ");
                }
                sb.Append($"→ picked {SampleRealGroundAt(new Vector3(x, 0f, z)):F2}");
                return sb.ToString();
            }
            catch (System.Exception ex)
            {
                return $"probe-ledger threw: {ex.Message}";
            }
        }

        // One conserved-volume relaxation pass. Each wet cell pushes water
        // toward lower-surface 4-neighbours proportionally to the surface
        // difference. Off-grid neighbours count as "own terrain height,
        // zero column" — boundary cells therefore bleed a little (runoff
        // over the grid edge, water lost), which reads as water escaping
        // downhill; the viscosity ramp stops it before everything drains.
        //
        // Returns the total amount of water moved this pass — the spring
        // sim uses it to detect steady state (flow front stopped).
        private float FlowPass(float[] column, float[] next, int w, int h, float viscosity)
        {
            float rate = VOLUME_FLOW_RATE * viscosity;
            if (rate <= 0.0005f) return 0f;

            System.Array.Copy(column, next, column.Length);
            float moved = 0f;

            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    float col = column[i];
                    if (col <= 0f) continue;

                    float surf = _terrainHeights[i] + col;

                    float d0 = SurfaceDelta(column, surf, x - 1, z, w, h, i);
                    float d1 = SurfaceDelta(column, surf, x + 1, z, w, h, i);
                    float d2 = SurfaceDelta(column, surf, x, z - 1, w, h, i);
                    float d3 = SurfaceDelta(column, surf, x, z + 1, w, h, i);
                    float total = d0 + d1 + d2 + d3;
                    if (total <= 0f) continue;

                    // Total outflow: proportional to the summed drop, hard
                    // capped at half the column per pass for stability.
                    float outflow = Mathf.Min(col * 0.5f, rate * total * 0.25f);
                    next[i] -= outflow;
                    moved += outflow;

                    // Distribute by share; off-grid shares are simply lost.
                    if (d0 > 0f) Deposit(next, x - 1, z, w, h, outflow * d0 / total);
                    if (d1 > 0f) Deposit(next, x + 1, z, w, h, outflow * d1 / total);
                    if (d2 > 0f) Deposit(next, x, z - 1, w, h, outflow * d2 / total);
                    if (d3 > 0f) Deposit(next, x, z + 1, w, h, outflow * d3 / total);
                }
            }

            System.Array.Copy(next, column, column.Length);
            return moved;
        }

        // Positive height drop from `surf` down to the neighbour's water
        // surface. Off-grid: pretend flat terrain at OUR cell's terrain
        // height with no water — a gentle constant drain at the boundary.
        private float SurfaceDelta(float[] column, float surf, int nx, int nz, int w, int h, int ownIdx)
        {
            float nSurf;
            if (nx < 0 || nx >= w || nz < 0 || nz >= h)
            {
                nSurf = _terrainHeights[ownIdx];
            }
            else
            {
                int ni = nz * w + nx;
                nSurf = _terrainHeights[ni] + column[ni];
            }
            return Mathf.Max(0f, surf - nSurf);
        }

        private static void Deposit(float[] next, int nx, int nz, int w, int h, float amount)
        {
            if (nx < 0 || nx >= w || nz < 0 || nz >= h) return; // runoff — lost
            next[nz * w + nx] += amount;
        }

        // Columns → the combiner's surface/depth grids. Cells thinner than
        // MIN_COLUMN get the -100 "no water" sentinel (mesh builder treats
        // <= -50 as empty).
        private void WriteColumnsToGrid(float[] column)
        {
            for (int i = 0; i < column.Length; i++)
            {
                if (column[i] >= VOLUME_MIN_COLUMN)
                {
                    _surfaceHeights[i] = _terrainHeights[i] + column[i];
                    _waterDepths[i] = column[i];
                }
                else
                {
                    _surfaceHeights[i] = -100f;
                    _waterDepths[i] = 0f;
                }
            }
        }

        // ── SPRING — infinite flowing source ─────────────────────────────
        //
        // Variant of the volume pour: instead of a fixed one-shot volume,
        // the source cells are REPLENISHED every pass (infinite spring)
        // and every cell is CAPPED at the layer thickness — excess always
        // passes downhill instead of pooling deeper. The stream crawls
        // down the slope until the flow front reaches equilibrium (or the
        // grid edge = SPRING_RANGE), then gels exactly like a volume.
        //
        // Known v1 limitation: the thickness cap means the stream cannot
        // fill a depression DEEPER than the layer thickness and overflow
        // out the far side — it stops there. Use a Water Volume in the
        // bowl if you want it filled. (Documented in the plan doc.)

        // How far the stream may run from the spring before the grid edge
        // drains it. Grid is capped at 128×128 cells, so bigger range =
        // coarser cells (80m extent ≈ 0.63m cells — fine for a stream).
        private const float SPRING_RANGE = 40f;

        // Flow-front equilibrium: gel when a pass moves less than this
        // much total water for STEADY_FRAMES consecutive frames.
        private const float SPRING_STEADY_EPSILON = 0.05f;
        private const int SPRING_STEADY_FRAMES = 20;

        // Hard timeout so a spring on endless flat ground still gels.
        private const float SPRING_MAX_SETTLE = 8f;

        // `fillY` = the IH ruler level (ground + PgUp/PgDn offset). The
        // spring POOL (source cells) fills up to this level (at least the
        // layer thickness), then the stream flows onward at `thickness`.
        // Round-59: added so the set level is meaningful for springs —
        // raise the ruler to get a deeper source pool that overflows.
        // Round-80 — bankOffset X: the water surface fills to (local bank
        // height − X) per cell, so the sheet follows the channel slope
        // instead of levelling to one flat Y (which vanilla LiquidVolume
        // did). fillY = the ruler source-pool level.
        public static WaterMeshCombiner CreateSpring(Vector3 center, float radius, float bankOffset, float fillY, CustomLiquidType liquid)
        {
            bankOffset = Mathf.Clamp(bankOffset, 0.2f, 10f);

            var disk = new WaterDisk(center, radius, maxDepth: Mathf.Max(2f, bankOffset), liquid);

            var go = new GameObject("WaterMeshCombiner_Spring");
            go.transform.position = disk.Position;

            var combiner = go.AddComponent<WaterMeshCombiner>();
            combiner.LiquidType = disk.LiquidType;
            combiner.CustomLiquidType = liquid;
            combiner._waterDisks.Add(disk);
            combiner._isSettling = true;
            combiner._needsRegeneration = false;

            s_allCombiners.Add(combiner);
            combiner.SetupDamageHandler();

            combiner.StartCoroutine(combiner.SpringFlowCoroutine(center, radius, bankOffset, fillY));

            FiresLogger.LogInfo($"[WaterMeshCombiner] CreateSpring at {center}, r={radius:F1}, bankOffset={bankOffset:F2}, fillY={fillY:F1}, liquid={liquid}");
            return combiner;
        }

        // Round-80 — per-cell local BANK height (the containing rim), used
        // to fill each cell to bank−X. Bank = the highest terrain within a
        // scan neighbourhood. Recomputed per spring after terrain sampling.
        private float[] _springBankHeights;
        private const int SPRING_BANK_SCAN = 8;   // cells to look for the rim (~5m at 0.6m cells)

        private void ComputeSpringBankHeights()
        {
            int w = _gridWidth, h = _gridHeight;
            _springBankHeights = new float[w * h];
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    float bank = _terrainHeights[z * w + x];
                    for (int dz = -SPRING_BANK_SCAN; dz <= SPRING_BANK_SCAN; dz++)
                    {
                        int nz = z + dz;
                        if (nz < 0 || nz >= h) continue;
                        for (int dx = -SPRING_BANK_SCAN; dx <= SPRING_BANK_SCAN; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= w) continue;
                            float t = _terrainHeights[nz * w + nx];
                            if (t > bank) bank = t;
                        }
                    }
                    _springBankHeights[z * w + x] = bank;
                }
            }
        }

        // Fill cap for a non-source cell (column metres): fill to (bank − X)
        // surface, i.e. depth = bank − X − terrain. Flat ground (bank ≈
        // terrain) gives a negative → clamped 0 → stays dry (no spill).
        private float SpringCellCap(int i, float bankOffset)
        {
            return Mathf.Max(0f, _springBankHeights[i] - bankOffset - _terrainHeights[i]);
        }

        private IEnumerator SpringFlowCoroutine(Vector3 center, float radius, float bankOffset, float fillY)
        {
            // Bounds: spring range in every direction so the stream can
            // run downhill well past the source circle.
            CalculateCombinedBounds();
            _combinedBounds.Expand(new Vector3(SPRING_RANGE * 2f, 0f, SPRING_RANGE * 2f));
            SetupGrid();
            // Round-67: fill-anchored PHYSICS probe (sees custom tiles,
            // rocks, pieces — GetGroundHeight is heightmap-blind).
            _groundProbeFillY = fillY;
            yield return SampleRealGroundAsync();
            // Round-80 — local bank heights drive the sloped fill cap.
            ComputeSpringBankHeights();

            int w = _gridWidth, h = _gridHeight;
            float[] column = new float[w * h];
            float[] next = new float[w * h];

            // Source mask: cells inside the ruler circle. Re-pinned every
            // frame (the infinite emitter) and EXEMPT from the thickness
            // cap — the pool may sit deeper than the stream layer when the
            // ruler level is raised.
            var isSource = new bool[w * h];
            var sourceCells = new List<int>();
            float r2 = radius * radius;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector3 wp = GridToWorld(x, z);
                    float dx = wp.x - center.x;
                    float dz = wp.z - center.z;
                    if (dx * dx + dz * dz <= r2)
                    {
                        int i = z * w + x;
                        isSource[i] = true;
                        sourceCells.Add(i);
                    }
                }
            }

            // Source column: fill the ruler pool up to the ruler level.
            for (int s = 0; s < sourceCells.Count; s++)
            {
                int i = sourceCells[s];
                column[i] = Mathf.Max(0.2f, fillY - _terrainHeights[i]);
            }

            WriteColumnsToGrid(column);
            BuildWaterMesh();
            SetupMaterial();
            LogVolumeDiagnostics("spring START", column, fillY);

            // ── Flow loop — full viscosity until steady, then gel ────────
            float t = 0f;
            int frame = 0;
            int steadyFrames = 0;
            while (t < SPRING_MAX_SETTLE && steadyFrames < SPRING_STEADY_FRAMES)
            {
                t += Time.deltaTime;

                float moved = 0f;
                for (int pass = 0; pass < VOLUME_SIM_PASSES_PER_FRAME; pass++)
                    moved += FlowPass(column, next, w, h, viscosity: 1f);

                // Replenish the source pool at the ruler level, then cap
                // NON-SOURCE cells at (bank − X). Because no cell can rise
                // above its bank cap, the continuously-fed excess spreads
                // ONWARD and downhill, filling each cell to bank−X — the
                // surface tracks the channel banks down the slope instead of
                // levelling flat. Flat ground caps at 0 → stays dry.
                for (int s = 0; s < sourceCells.Count; s++)
                {
                    int i = sourceCells[s];
                    column[i] = Mathf.Max(0.2f, fillY - _terrainHeights[i]);
                }
                for (int i = 0; i < column.Length; i++)
                {
                    if (isSource[i]) continue;
                    float cap = SpringCellCap(i, bankOffset);
                    if (column[i] > cap) column[i] = cap;
                }

                steadyFrames = moved < SPRING_STEADY_EPSILON ? steadyFrames + 1 : 0;

                frame++;
                if (frame % VOLUME_REBUILD_EVERY_N_FRAMES == 0)
                {
                    WriteColumnsToGrid(column);
                    BuildWaterMesh();
                }

                yield return null;
            }

            // ── Gel — identical tail to the volume pour ──────────────────
            WriteColumnsToGrid(column);
            _isSettling = false;
            _isGelled = true;

            BuildWaterMesh();
            SetupMaterial();
            SetupSwimTrigger();
            DetectCliffEdges();
            SpawnAmbientVFX();

            MarkAllRowsDirty();
            EnsureAnchorExists();
            SaveToAnchor();

            FiresLogger.LogInfo($"[WaterMeshCombiner] Spring gelled | bankOffset={bankOffset:F2} | settled in {t:F1}s ({(steadyFrames >= SPRING_STEADY_FRAMES ? "steady-state" : "timeout")})");
            LogVolumeDiagnostics("spring GELLED", column, fillY);
        }

        // ── Real-ground sampling (round-60) ──────────────────────────────
        //
        // ROOT CAUSE of "swimmable but invisible" volumes/springs: the
        // combiner's SampleTerrainHeightAt deliberately uses
        // WorldGenerator.GetHeight — the PROCEDURAL height, ignoring all
        // vanilla TerrainComp modifications (player dug/raised ground).
        // That's a reasonable choice for thin drawn disks (avoids bank
        // ride-up), but fatal for the column sims: on modified terrain
        // the water columns get built against ground metres below the
        // real surface → the finished mesh is BURIED under the visible
        // dirt (invisible) while the swim-trigger boxes span upward far
        // enough to poke above it (swimmable). Diagnosed from the
        // round-59 log: terrain=[21.3..33.8] under a flat dug area the
        // player stood on at Y≈30.
        //
        // Round-67 — PHYSICS-PROBE ground sampling. The round-60 fix used
        // ZoneSystem.GetGroundHeight and its comment claimed that covers
        // custom tiles — it does NOT. GetGroundHeight is HEIGHTMAP-ONLY
        // (proven 2026-07-06, the companion ground-snap bug): it is blind
        // to FAT custom terrain, rocks, and build pieces, and disagrees
        // with the visible ground by METERS. Result (07-10 test, spring on
        // a built cliff): terrain=[23.3..56.6] = the meadow heightmap
        // under the build → water columns filled from ~21m below the
        // player's feet → water far below the ruler, vertical mesh sheets
        // up the cliff face, swim triggers wrapping the wrong space.
        //
        // The probe is anchored to the FILL LEVEL, not "way up in the
        // sky", so floating islands overhead don't hijack the sample:
        //   * cast down from fillY + 40 across fillY − 200
        //   * highest hit AT/BELOW fill+0.25 → the ground water rests on
        //   * hits only ABOVE fill → cell is blocking land (dry) — return
        //     the lowest such surface
        //   * no hits at all → heightmap rescue, then procedural
        private const float GROUND_PROBE_MARGIN = 40f;
        private const float GROUND_PROBE_DEPTH = 200f;

        // Fill level the current settle/spring sim is anchored to. NaN →
        // fall back to the combiner's own Y (drawn-disk regen paths).
        private float _groundProbeFillY = float.NaN;

        private static readonly RaycastHit[] s_groundProbeHits = new RaycastHit[16];
        private static int s_groundProbeMask = -1;

        private float SampleRealGroundAt(Vector3 wp)
        {
            if (s_groundProbeMask < 0)
                s_groundProbeMask = LayerMask.GetMask(
                    "Default", "static_solid", "piece", "terrain", "Default_small");

            float fill = float.IsNaN(_groundProbeFillY) ? transform.position.y : _groundProbeFillY;
            var origin = new Vector3(wp.x, fill + GROUND_PROBE_MARGIN, wp.z);
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, s_groundProbeHits,
                GROUND_PROBE_MARGIN + GROUND_PROBE_DEPTH, s_groundProbeMask,
                QueryTriggerInteraction.Ignore);

            float bestBelow = float.NegativeInfinity;
            float lowestAbove = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                float y = s_groundProbeHits[i].point.y;
                if (y <= fill + 0.25f)
                {
                    if (y > bestBelow) bestBelow = y;
                }
                else if (y < lowestAbove)
                {
                    lowestAbove = y;
                }
            }
            if (!float.IsNegativeInfinity(bestBelow)) return bestBelow;
            if (!float.IsPositiveInfinity(lowestAbove)) return lowestAbove;

            // Probe missed entirely (unloaded colliders at grid edge) —
            // heightmap rescue, then procedural.
            if (ZoneSystem.instance != null
                && ZoneSystem.instance.GetGroundHeight(wp, out float groundY))
            {
                return groundY;
            }
            if (WorldGenerator.instance != null)
                return WorldGenerator.instance.GetHeight(wp.x, wp.z);
            return wp.y;
        }

        private IEnumerator SampleRealGroundAsync()
        {
            const int BATCH_SIZE = 500;
            int processed = 0;
            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int idx = z * _gridWidth + x;
                    _terrainHeights[idx] = SampleRealGroundAt(GridToWorld(x, z));

                    processed++;
                    if (processed % BATCH_SIZE == 0)
                        yield return null;
                }
            }
        }

        // Regen path for GELLED combiners: the stored surface grid is the
        // truth — recompute depths from it and rebuild the mesh, skipping
        // the whole IDW/settle/clip pipeline (which would clobber the
        // settled shape with disk-derived values). Same shape as the tail
        // of RestoreFromAnchorData.
        internal void RebuildFromStoredGrid()
        {
            if (_surfaceHeights == null || _surfaceHeights.Length == 0) return;

            int n = _gridWidth * _gridHeight;
            if (n <= 0 || _surfaceHeights.Length != n) return;

            if (_waterDepths == null || _waterDepths.Length != n)
                _waterDepths = new float[n];

            bool terrainOk = _terrainHeights != null && _terrainHeights.Length == n;
            for (int i = 0; i < n; i++)
            {
                float s = _surfaceHeights[i];
                if (s < -50f)
                {
                    _waterDepths[i] = 0f;
                    continue;
                }
                float terrain = terrainOk ? _terrainHeights[i] : s - 5f;
                _waterDepths[i] = Mathf.Max(0f, s - terrain);
            }

            BuildWaterMesh();
            SetupMaterial();
            SetupSwimTrigger();
        }
    }
}
