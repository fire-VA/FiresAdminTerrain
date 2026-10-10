using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Performance
{
    // Spreads ClutterSystem grass-patch generation across frames.
    //
    // THE PROBLEM: vanilla builds a whole 8m grass patch in ONE frame. A patch is
    // ~2,000-4,000 grass candidates, each a downward Physics.Raycast — so stepping
    // onto fresh ground costs a single 20-80ms frame. Cross a patch boundary every
    // ~8m while moving and that's a steady stutter; a full-ring rebuild (dungeon
    // exit, terrain edit) does all ~120 patches at once.
    //
    // THE FIX (no change to how grass LOOKS): intercept GenerateVegPatch so it
    // registers an EMPTY but valid patch immediately (vanilla adds it to m_patches,
    // zero raycasts that frame) and queues a Builder. A post-LateUpdate driver then
    // fills the queued patches incrementally under a per-frame time budget, doing
    // the SAME raycasts + filters vanilla would, just amortized. Grass fills over a
    // handful of frames instead of spiking one.
    //
    // Fidelity: the inner placement loop is a faithful transcription of vanilla
    // GenerateVegPatch (same per-clutter RNG seed, same filters, same Instantiate/
    // AddInstance) — checkpointed only at whole-clutter-type boundaries so each
    // type's deterministic RNG sequence is never split. Global Random state is
    // saved/restored around each driver pass so nothing else is perturbed.
    //
    // Fail-safe: if reflection can't bind (game update moved a private member) it
    // sets s_reflectionFailed and the prefix returns true — vanilla builds patches
    // synchronously as before. Toggle off via Enabled.
    public static class ClutterSpread
    {
        public static bool Enabled = true;

        // Soft per-frame ceiling for the fill driver. One steady patch fills in a
        // few frames well under this; a full-ring rebuild drains over a second or
        // two instead of freezing. Whole clutter types are atomic, so a single
        // frame may overshoot slightly by one type — intentional, keeps RNG intact.
        private const float FrameBudgetMs = 4f;

        private sealed class Builder
        {
            public Vector2Int Id;
            public object Patch;            // ClutterSystem.PatchData (private nested)
            public List<GameObject> Objects;
            public Vector3 Center;
            public float HalfSize;
            public Heightmap.Biome Biomes;
            public int ClutterIndex;        // resume cursor — next clutter type to place
            public int InstanceIndex;       // resume cursor WITHIN the current type (hard budget)
            public bool MidType;            // true = InstanceIndex/RngState are live
            public UnityEngine.Random.State RngState;   // RNG snapshot for deterministic mid-type resume
            public float EnqueuedAt;        // queue→done latency accounting
            // ClutterSystem.m_waterLevel while the GenerateVegPatch hook held the patch: the water-ecology move is
            // restored before this builder runs.
            public float WaterLevel;
            // 0.2.824 (Fire 10-07: grass blanked on every cave crossing): a RESET patch's old grass, kept drawn until this
            // builder has filled the replacement, then destroyed (build-then-swap). Null for a fresh patch.
            public List<GameObject> Retiring;
            // 0.2.828: the cave light at this patch (CaveLight), NaN until its first cave-floor candidate.
            public float Light = float.NaN;
        }

        // A deterministic 0..1 from a candidate's position and type (does not touch UnityEngine.Random, so vanilla's sequence holds).
        private static float CandidateHash(Vector3 p, int type)
        {
            unchecked
            {
                uint h = (uint)(Mathf.RoundToInt(p.x * 8f) * 73856093 ^ Mathf.RoundToInt(p.z * 8f) * 19349663 ^ type * 83492791);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        private static void DestroyRetiring(Builder b)
        {
            if (b?.Retiring == null) return;
            foreach (var go in b.Retiring) if (go != null) UnityEngine.Object.Destroy(go);
            b.Retiring = null;
        }

        private static readonly Dictionary<Vector2Int, Builder> s_builders =
            new Dictionary<Vector2Int, Builder>();
        private static readonly List<Vector2Int> s_tmpKeys = new List<Vector2Int>();

        // Throughput accounting — answers "why does clutter take minutes"
        // with counters instead of theories. One line per 5s window whenever
        // there was any activity (or any starvation). STARVED = frames the
        // driver refused to run because a nearby heightmap had a queued
        // rebuild (vanilla's own generation gate; if voxel work re-pokes
        // heightmaps continuously this shows up here as the smoking gun).
        private static float s_winStart = -1f;
        private static int s_winFrames, s_winStarved, s_winQueued, s_winDone, s_winPlaced;
        private static double s_winDriverMs, s_winLatencySum;
        private static float s_oldestEnq = float.MaxValue;

        private static void FlushWindow()
        {
            float now = Time.unscaledTime;
            if (s_winStart < 0f) { s_winStart = now; return; }
            // 60s cadence (was 5s — the raycast-storm hunt is over; during
            // streaming there is ALWAYS activity, so 5s printed constantly).
            if (now - s_winStart < 60f) return;
            if (s_winQueued > 0 || s_winDone > 0 || s_winStarved > 0 || s_builders.Count > 0)
            {
                float oldestAge = 0f;
                foreach (var b in s_builders.Values)
                    if (now - b.EnqueuedAt > oldestAge) oldestAge = now - b.EnqueuedAt;
                string lat = s_winDone > 0 ? $"{s_winLatencySum / s_winDone:0.0}s" : "-";
                FiresLogger.LogVerbose($"[FiresClutter] 60s: pending {s_builders.Count}(oldest {oldestAge:0.0}s) | " +
                          $"+{s_winQueued} queued, {s_winDone} done (avg latency {lat}) | " +
                          $"placed {s_winPlaced:N0} inst | driver {s_winDriverMs:0.0}ms over {s_winFrames} frames | " +
                          $"STARVED {s_winStarved}/{s_winFrames + s_winStarved} (hm rebuild queued)");
            }
            s_winStart = now;
            s_winFrames = 0; s_winStarved = 0; s_winQueued = 0; s_winDone = 0; s_winPlaced = 0;
            s_winDriverMs = 0; s_winLatencySum = 0;
        }

        // Reflection — only the genuinely private members; the rest of ClutterSystem
        // (m_clutter / m_amountScale / m_waterLevel / m_distance / m_grassPatchSize /
        // m_menuHack) is public and accessed directly.
        private static FieldInfo f_quality, f_grassRoot, f_patches;
        private static MethodInfo m_getPatchBiomes, m_allocatePatch;
        private static FieldInfo f_pdObjects, f_pdCenter;
        private static bool s_reflectionReady, s_reflectionFailed, s_loggedError;

        private static void EnsureReflection()
        {
            if (s_reflectionReady || s_reflectionFailed) return;
            try
            {
                var t = typeof(ClutterSystem);
                const BindingFlags bf = BindingFlags.NonPublic | BindingFlags.Instance;
                f_quality       = t.GetField("m_quality", bf);
                f_grassRoot     = t.GetField("m_grassRoot", bf);
                f_patches       = t.GetField("m_patches", bf);
                m_getPatchBiomes = t.GetMethod("GetPatchBiomes", bf);
                m_allocatePatch  = t.GetMethod("AllocatePatch", bf);
                var tPatch = t.GetNestedType("PatchData", BindingFlags.NonPublic);
                f_pdObjects = tPatch?.GetField("m_objects", BindingFlags.Public | BindingFlags.Instance);
                f_pdCenter  = tPatch?.GetField("center", BindingFlags.Public | BindingFlags.Instance);

                if (f_quality == null || f_grassRoot == null || f_patches == null
                    || m_getPatchBiomes == null || m_allocatePatch == null
                    || f_pdObjects == null || f_pdCenter == null)
                {
                    s_reflectionFailed = true;
                    FiresLogger.LogWarning("[ClutterSpread] reflection bind incomplete — " +
                        "falling back to vanilla synchronous patch generation.");
                    return;
                }
                s_reflectionReady = true;
            }
            catch (Exception ex)
            {
                s_reflectionFailed = true;
                FiresLogger.LogWarning("[ClutterSpread] reflection failed — vanilla fallback: " + ex.Message);
            }
        }

        // GenerateVegPatch prefix: register an empty patch + queue a builder, skip
        // the original. Returns false to suppress vanilla's synchronous build.
        internal static bool OnGenerateVegPatch(ClutterSystem cs, Vector2Int patchID, float size, ref object result)
        {
            if (!Enabled) return true;
            EnsureReflection();
            if (s_reflectionFailed) return true;        // vanilla fallback
            if (cs.m_menuHack) return true;             // menu grass: let vanilla do it synchronously

            Vector3 center = cs.GetVegPatchCenter(patchID);
            float halfSize = size / 2f;

            var biomes = (Heightmap.Biome) m_getPatchBiomes.Invoke(cs, new object[] { center, halfSize });
            if (biomes == Heightmap.Biome.None)
            {
                result = null;                          // exactly vanilla's "no patch here"
                return false;
            }

            object patch = m_allocatePatch.Invoke(cs, null);
            f_pdCenter.SetValue(patch, center);
            var objects = (List<GameObject>) f_pdObjects.GetValue(patch);

            // Build-then-swap: vanilla destroys the old patch's objects right after this returns. Take them out of its list
            // (vanilla then destroys nothing) and keep them until the new patch is filled. If an earlier swap was still
            // pending here, ITS old (complete) grass stays the stand-in and the half-built patch goes as vanilla meant.
            List<GameObject> retiring = null;
            var live = (IDictionary) f_patches.GetValue(cs);
            if (s_builders.TryGetValue(patchID, out Builder pending) && pending.Retiring != null)
            {
                retiring = pending.Retiring;
                pending.Retiring = null;
            }
            else if (live != null && live.Contains(patchID))
            {
                var old = (List<GameObject>) f_pdObjects.GetValue(live[patchID]);
                if (old != null && old.Count > 0) { retiring = new List<GameObject>(old); old.Clear(); }
            }

            s_builders[patchID] = new Builder
            {
                Retiring = retiring,
                Id = patchID,
                Patch = patch,
                Objects = objects,
                Center = center,
                HalfSize = halfSize,
                Biomes = biomes,
                ClutterIndex = 0,
                EnqueuedAt = Time.unscaledTime,
                WaterLevel = cs.m_waterLevel,
            };
            s_winQueued++;

            result = patch;                             // vanilla adds this empty patch to m_patches
            return false;
        }

        // Per-frame fill driver (runs as a LateUpdate postfix, after vanilla has
        // queued at most one new patch this frame). Drains queued builders under
        // the time budget.
        internal static void OnLateUpdatePostfix(ClutterSystem cs)
        {
            FlushWindow();
            // Grass render-distance override (0 = keep vanilla). Grass tris
            // scale with distance², so trimming the reach is the single biggest
            // lever on the forest-floor carpet. Applied before the early-outs so
            // it takes effect even on idle frames.
            float grassDist = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.GrassRenderDistance?.Value ?? 0f;
            if (grassDist > 1f && cs.m_distance != grassDist) cs.m_distance = grassDist;
            if (!Enabled || s_reflectionFailed || s_builders.Count == 0) return;

            // Faithful to vanilla: don't place clutter while a nearby heightmap is
            // mid-rebuild (its collider can transiently miss raycasts). Vanilla
            // gates all generation on this; we gate the deferred fill the same way.
            Camera cam = Utils.GetMainCamera();
            if (cam == null) return;
            if (Heightmap.HaveQueuedRebuild(cam.transform.position, cs.m_distance)) { s_winStarved++; s_starvedTotal++; return; }
            s_winFrames++;

            var grassRoot = (GameObject) f_grassRoot.GetValue(cs);
            if (grassRoot == null) return;
            var quality = (ClutterSystem.Quality) f_quality.GetValue(cs);
            var patches = (IDictionary) f_patches.GetValue(cs);

            var sw = Stopwatch.StartNew();
            UnityEngine.Random.State rng = UnityEngine.Random.state;
            try
            {
                s_tmpKeys.Clear();
                s_tmpKeys.AddRange(s_builders.Keys);
                for (int i = 0; i < s_tmpKeys.Count; i++)
                {
                    Vector2Int key = s_tmpKeys[i];
                    if (!s_builders.TryGetValue(key, out Builder b)) continue;

                    // Drop the builder if vanilla retired/replaced its patch
                    // (timeout, ClearAll, ResetGrass re-gen). m_objects we already
                    // added are owned by vanilla's PatchData and cleaned up by it.
                    object live = patches[key];
                    if (live == null || !ReferenceEquals(live, b.Patch))
                    {
                        DestroyRetiring(b);
                        s_builders.Remove(key);
                        continue;
                    }

                    try
                    {
                        if (AdvanceBuilder(cs, b, quality, grassRoot, sw))
                        {
                            DestroyRetiring(b);
                            s_builders.Remove(key);
                            s_winDone++;
                            s_winLatencySum += Time.unscaledTime - b.EnqueuedAt;
                        }
                    }
                    catch (Exception ex)
                    {
                        DestroyRetiring(b);
                        s_builders.Remove(key);
                        if (!s_loggedError)
                        {
                            s_loggedError = true;
                            FiresLogger.LogWarning("[ClutterSpread] builder error (dropping patch): " + ex);
                        }
                    }

                    if (sw.Elapsed.TotalMilliseconds >= FrameBudgetMs) break;
                }
            }
            finally
            {
                UnityEngine.Random.state = rng;
                s_winDriverMs += sw.Elapsed.TotalMilliseconds;
            }
        }

        // Place whole clutter types from the resume cursor until the frame budget
        // is hit. Returns true when the patch is fully built.
        private static bool AdvanceBuilder(ClutterSystem cs, Builder b, ClutterSystem.Quality quality,
            GameObject grassRoot, Stopwatch sw)
        {
            List<ClutterSystem.Clutter> clutter = cs.m_clutter;
            int count = clutter.Count;
            // Fires clutter density lever — scales the whole grass carpet down
            // (config hot-reloads on the next patch regen). The dense forest
            // floor at vanilla density was tens of millions of tris.
            float densityScale = VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.ClutterDensityScale?.Value ?? 1f;
            float amountScale = cs.m_amountScale * densityScale;
            float waterLevel = b.WaterLevel;
            float lodCap = cs.m_distance - cs.m_grassPatchSize / 2f;

            while (b.ClutterIndex < count)
            {
                // HARD budget: PlaceClutterType is resumable MID-TYPE. The
                // old between-types check made the budget soft — one heavy
                // type (hundreds of raycast instances, ×128 registered
                // clutter entries after the FAP re-injection) still burned a
                // 20-80ms frame. Now no frame ever exceeds the budget by more
                // than one 8-instance slice.
                if (!PlaceClutterType(cs, b, clutter[b.ClutterIndex], b.ClutterIndex,
                    quality, amountScale, waterLevel, grassRoot, lodCap, sw))
                    return false;                       // budget hit mid-type; resume next frame
                b.ClutterIndex++;
                b.InstanceIndex = 0;
                b.MidType = false;
                if (sw.Elapsed.TotalMilliseconds >= FrameBudgetMs) break;
            }
            return b.ClutterIndex >= count;
        }

        // Faithful transcription of vanilla ClutterSystem.GenerateVegPatch's inner
        // per-clutter body (assembly_valheim ClutterSystem.cs:260-353). Same RNG
        // seed, same filter order, same placement. Only the framing differs.
        // Returns FALSE when the frame budget was hit mid-type (the builder
        // resumes at b.InstanceIndex with the saved RNG state next frame —
        // instance placement stays byte-identical to a single-frame run).
        private static bool PlaceClutterType(ClutterSystem cs, Builder b, ClutterSystem.Clutter c, int index1,
            ClutterSystem.Quality quality, float amountScale, float waterLevel, GameObject grassRoot, float lodCap,
            Stopwatch sw)
        {
            if (!c.m_enabled || (b.Biomes & c.m_biome) == Heightmap.Biome.None) return true;
            // 0.2.693: a patch with no voxel edit anchor within reach skips the per-candidate edit lookup.
            bool patchNearEdits = global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.HasEditsXZ(b.Center.x, b.Center.z, b.HalfSize * 1.5f);

            InstanceRenderer instanceRenderer = null;
            if (b.MidType && b.InstanceIndex > 0)
            {
                UnityEngine.Random.state = b.RngState;   // resume mid-type, deterministic
                // re-find the shared InstanceRenderer this type already spawned
                for (int oi = b.Objects.Count - 1; oi >= 0 && instanceRenderer == null; oi--)
                {
                    var go0 = b.Objects[oi];
                    if (go0 != null && go0.name.StartsWith(c.m_prefab.name))
                        instanceRenderer = go0.GetComponent<InstanceRenderer>();
                }
            }
            else
            {
                UnityEngine.Random.InitState(b.Id.x * (b.Id.y * 1374) + index1 * 9321);
            }
            Vector3 fractalOffset = new Vector3(c.m_fractalOffset, 0f, 0f);
            float cosMaxTilt = Mathf.Cos((float)Math.PI / 180f * c.m_maxTilt);
            float cosMinTilt = Mathf.Cos((float)Math.PI / 180f * c.m_minTilt);

            int baseAmount = quality == ClutterSystem.Quality.Low ? c.m_amount / 4
                           : quality == ClutterSystem.Quality.Med ? c.m_amount / 2
                           : c.m_amount;
            // ROUND, don't truncate: sparse types (m_amount 1-4) truncated to
            // ZERO at mid sliders — whole clutter species vanished in a step
            // instead of thinning with the percentage.
            int amount = Mathf.RoundToInt(baseAmount * amountScale);

            Vector3 center = b.Center;
            float halfSize = b.HalfSize;

            for (int i = b.InstanceIndex; i < amount; i++)
            {
                if ((i & 7) == 0 && i > b.InstanceIndex
                    && sw.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                {
                    b.InstanceIndex = i;
                    b.MidType = true;
                    b.RngState = UnityEngine.Random.state;
                    return false;
                }
                Vector3 pos = new Vector3(
                    UnityEngine.Random.Range(center.x - halfSize, center.x + halfSize), 0f,
                    UnityEngine.Random.Range(center.z - halfSize, center.z + halfSize));
                float rotY = UnityEngine.Random.Range(0, 360);
                // 0.2.693 (Fire 10-01: "no more grass over edited terrain at all"): which vanilla gate refuses each candidate,
                // split by whether it stands on voxel-EDITED ground (a VAVoxel edit anchor's chunk). Read by clutterfloat_test.
                bool edited = patchNearEdits && global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.HasEditsXZ(pos.x, pos.z, 0f);

                if (c.m_inForest)
                {
                    float ff = WorldGenerator.GetForestFactor(pos);
                    if (ff < c.m_forestTresholdMin || ff > c.m_forestTresholdMax) { GateCount(edited, Gate.Forest); continue; }
                }
                if (c.m_fractalScale > 0f)
                {
                    float fb = Utils.Fbm(pos * 0.01f * c.m_fractalScale + fractalOffset, 3, 1.6f, 0.7f);
                    if (fb < c.m_fractalTresholdMin || fb > c.m_fractalTresholdMax) { GateCount(edited, Gate.Fractal); continue; }
                }

                if (!cs.GetGroundInfo(pos, out Vector3 point, out Vector3 normal, out Heightmap hmap, out Heightmap.Biome biome))
                { GateCount(edited, Gate.NoGround); continue; }
                // 0.2.694: from here on, "edited" is the ground itself, off the generated height (FatGroundProbe.IsGroundEdited).
                edited = global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.IsGroundEdited(point.x, point.z, point.y);
                if ((c.m_biome & biome) == Heightmap.Biome.None) { GateCount(edited, Gate.Biome); continue; }

                float alt = point.y - waterLevel;
                if (alt < c.m_minAlt || alt > c.m_maxAlt) { GateCount(edited, Gate.Altitude); continue; }
                if (normal.y < cosMaxTilt || normal.y > cosMinTilt) { GateCount(edited, Gate.Tilt); continue; }

                if (c.m_minOceanDepth != c.m_maxOceanDepth)
                {
                    // Vanilla's ocean depth is the ZONE's depth under the sea (Heightmap.UpdateCornerDepths: max(0, 30 - height)
                    // at the heightmap's four corners, lerped). It keeps a clutter such as instanced_waterlilies (ocean depth
                    // 0..1) out of the open sea, and it reads 0 at an inland pond, which is right as it is. 0.2.662-0.2.676
                    // replaced it with the pond's local depth, which refused every lily deep enough for its altitude band
                    // (bed 3.2-4.5 m under the surface): removed in 0.2.677 (R47, 10-01).
                    float od = hmap.GetOceanDepth(pos);
                    if (od < c.m_minOceanDepth || od > c.m_maxOceanDepth) { GateCount(edited, Gate.OceanDepth); continue; }
                }
                // 0.2.798 (layered clutter): on a cave floor the heightmap's mask is the SURFACE's above it; the cave's own hoe /
                // cultivator paint (VoxelLayerPaint) decides instead: painted = cleared, no vegetation.
                bool caveFloor = ClutterGroundProbe.CameraInCave;
                byte cavePaint = caveFloor ? global::VerdantsAscent.Terrain.Voxel.VoxelLayerPaint.PaintedMaskAt(point) : (byte)0;
                if (c.m_minVegetation != c.m_maxVegetation)
                {
                    float vm = caveFloor ? (cavePaint != 0 ? 0f : 1f) : hmap.GetVegetationMask(point);
                    if (vm > c.m_maxVegetation || vm < c.m_minVegetation) { GateCount(edited, Gate.VegetationMask); continue; }
                }
                if (!c.m_onCleared || !c.m_onUncleared)
                {
                    bool cleared = caveFloor ? cavePaint != 0 : hmap.IsCleared(point);
                    if ((c.m_onCleared && !cleared) || (c.m_onUncleared & cleared)) { GateCount(edited, Gate.Cleared); continue; }
                }
                // 0.2.828 (Fire 10-07, cave clutter by light): in a cave the biome's clutter keeps 'Dark density' of its pieces at
                // 'Dark scale' where no light reaches, rising to all of them at full size toward mouths, shafts and sinkholes.
                float lightScale = 1f;
                if (caveFloor)
                {
                    if (float.IsNaN(b.Light)) b.Light = global::VerdantsAscent.Terrain.Voxel.CaveLight.At(point);
                    float keep = Mathf.Lerp(global::VerdantsAscent.Terrain.Voxel.CaveLight.DarkDensity, 1f, b.Light);
                    if (CandidateHash(pos, index1) >= keep) { GateCount(edited, Gate.Cleared); continue; }
                    lightScale = Mathf.Lerp(global::VerdantsAscent.Terrain.Voxel.CaveLight.DarkScale, 1f, b.Light);
                }
                GateCount(edited, Gate.Placed);

                pos = point;
                if (c.m_snapToWater) pos.y = waterLevel;
                if (c.m_randomOffset != 0f) pos.y += UnityEngine.Random.Range(-c.m_randomOffset, c.m_randomOffset);

                Quaternion rot = !c.m_terrainTilt
                    ? Quaternion.Euler(0f, rotY, 0f)
                    : Quaternion.AngleAxis(rotY, normal);

                if (c.m_instanced)
                {
                    if (instanceRenderer == null)
                    {
                        GameObject go = UnityEngine.Object.Instantiate(c.m_prefab, center, Quaternion.identity, grassRoot.transform);
                        instanceRenderer = go.GetComponent<InstanceRenderer>();
                        if (instanceRenderer.m_lodMaxDistance > lodCap) instanceRenderer.m_lodMaxDistance = lodCap;
                        b.Objects.Add(go);
                    }
                    float scale = UnityEngine.Random.Range(c.m_scaleMin, c.m_scaleMax) * lightScale;
                    instanceRenderer.AddInstance(pos, rot, scale);
                    s_winPlaced++;
                }
                else
                {
                    GameObject go = UnityEngine.Object.Instantiate(c.m_prefab, pos, rot, grassRoot.transform);
                    if (lightScale != 1f) go.transform.localScale *= lightScale;
                    b.Objects.Add(go);
                    s_winPlaced++;
                }
            }
            return true;
        }

        // ── 0.2.693: the gate census (Fire 10-01, bare grass over edited ground) ─────────────────────────────────────
        internal enum Gate { Forest, Fractal, NoGround, Biome, Altitude, Tilt, OceanDepth, VegetationMask, Cleared, Placed }
        private static readonly int[] s_gateEdited = new int[10], s_gatePlain = new int[10];

        private static void GateCount(bool edited, Gate g) { if (edited) s_gateEdited[(int)g]++; else s_gatePlain[(int)g]++; }

        // "placed N of M (P %); refused: gate xN, …" for candidates on edited / plain ground since the last reset.
        internal static string GateCensus(bool edited)
        {
            var a = edited ? s_gateEdited : s_gatePlain;
            int total = 0;
            for (int i = 0; i < a.Length; i++) total += a[i];
            if (total == 0) return "no candidates";
            int placed = a[(int)Gate.Placed];
            var parts = new List<string>();
            for (int i = 0; i < a.Length; i++)
                if (i != (int)Gate.Placed && a[i] > 0) parts.Add($"{(Gate)i} x{a[i]}");
            return $"placed {placed} of {total} ({100f * placed / total:0} %); refused: {(parts.Count > 0 ? string.Join(", ", parts) : "none")}";
        }

        // 0.2.693: builders waiting, and frames the fill was held back by a queued heightmap rebuild (since the census reset).
        internal static int s_starvedTotal;
        internal static int BuildersPending => s_builders.Count;

        internal static void ResetGateCensus() { s_starvedTotal = 0; System.Array.Clear(s_gateEdited, 0, s_gateEdited.Length); System.Array.Clear(s_gatePlain, 0, s_gatePlain.Length); }

        internal static void OnClearAll() { foreach (var b in s_builders.Values) DestroyRetiring(b); s_builders.Clear(); }
        internal static void OnAwake() { foreach (var b in s_builders.Values) DestroyRetiring(b); s_builders.Clear(); }

        // ── Harmony hooks (thin — all logic lives above) ──────────────────────
        // OnGenerateVegPatch runs from FAT's one ClutterSystem.GenerateVegPatch hook (FatVanillaHooks).

        [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
        private static class LateUpdate_Patch
        {
            [HarmonyPostfix]
            private static void Postfix(ClutterSystem __instance)
            {
                VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.RebuildWhenWaterKnown(__instance);
                OnLateUpdatePostfix(__instance);
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), "ClearAll")]
        private static class ClearAll_Patch
        {
            [HarmonyPostfix]
            private static void Postfix() => OnClearAll();
        }

        [HarmonyPatch(typeof(ClutterSystem), "Awake")]
        private static class Awake_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                OnAwake();
                VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.ForgetWater();
            }
        }
    }
}
