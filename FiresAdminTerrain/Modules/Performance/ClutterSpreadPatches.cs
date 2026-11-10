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
            // 0.2.1072 ([seasons]): the patch sits on our water: underwater bands take shallow beds (FiresClutterWaterEcology.WideMaxAlt)
            public bool UnderwaterWide;
            // Grass must not blank on every cave crossing: a RESET patch's old grass, kept drawn until this
            // builder has filled the replacement, then destroyed (build-then-swap). Null for a fresh patch.
            public List<GameObject> Retiring;
            // The cave light at this patch (CaveLight), NaN until its first cave-floor candidate.
            public float Light = float.NaN;
            // 0.2.1348: the highest water around this patch, NaN until a water band first asks (the cheap gate before a candidate's own water read).
            public float WaterTop = float.NaN;
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
                UnderwaterWide = VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.UnderwaterWide,
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
        // 0.2.1083 (Fire: "still no lillies vass"): every water-band candidate (an underwater or snap-to-water clutter) in a patch on
        // our water, by outcome: past the biome + altitude gates, refused by biome, refused by altitude (with the last alt / band).
        private static int s_wbPassed, s_wbBiome, s_wbAlt;
        private static string s_wbLast = "";
        private static float s_wbLogAt;
        private static void WaterBandNote(ClutterSystem.Clutter c, int outcome, float alt = 0f, float maxAlt = 0f)
        {
            if (outcome == 0) s_wbPassed++;
            else if (outcome == 1) { s_wbBiome++; s_wbLast = $"{c.m_name} refused by biome"; }
            else { s_wbAlt++; s_wbLast = $"{c.m_name} alt {alt:0.00} outside {c.m_minAlt:0.00}..{maxAlt:0.00}"; }
            if (Time.unscaledTime < s_wbLogAt) return;
            s_wbLogAt = Time.unscaledTime + 30f;
            FiresLogger.LogDebug($"[FiresWater] water plants on our water last 30 s: {s_wbPassed} candidate(s) past biome + altitude, {s_wbBiome} refused by biome, {s_wbAlt} by altitude (last: {s_wbLast})");
            s_wbPassed = s_wbBiome = s_wbAlt = 0;
        }

        // 0.2.1081: our water stands over this instance point (water surface above the ground there); only asked in patches on our water.
        private static int s_landUnderWater;
        private static float s_nextLandLog;
        private static bool UnderOurWater(Vector3 point)
        {
            if (Time.unscaledTime >= s_nextLandLog && s_landUnderWater > 0)
            {
                s_nextLandLog = Time.unscaledTime + 30f;
                if (VerdantsAscent.Terrain.Voxel.FatDiag.WaterClutter) FiresLogger.LogDebug($"[FiresWater] clutter: {s_landUnderWater} land clutter instance(s) refused under our water (vass/lilies only there) last 30 s");
                s_landUnderWater = 0;
            }
            return VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(point.x, point.z, 0.05f, false, out float surface, out _) && surface > point.y + 0.05f;
        }

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
        // 0.2.1094: a lily pad's footprint (vanilla pads are ~0.5 m across at scale 1).
        private const float PadReach = 0.6f;

        // The highest water surface over a pad's footprint: the centre plus 4 points at PadReach (FatWaterChecks asks the placed
        // water field first, then the generated water). Points with no water, or more than 1 m off the centre's level, are skipped,
        // so a pad at a shore never borrows another body's level. Returns `centre` when nothing higher is found.
        private static float FootprintSurface(float x, float z, float centre)
        {
            float best = centre;
            for (int i = 0; i < 5; i++)
            {
                float px = x + (i == 1 ? PadReach : i == 2 ? -PadReach : 0f);
                float pz = z + (i == 3 ? PadReach : i == 4 ? -PadReach : 0f);
                if (!global::VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(px, pz, 0.05f, false, out float s, out _)) continue;
                if (Mathf.Abs(s - centre) <= 1f && s > best) best = s;
            }
            return best;
        }

        // 0.2.1348 (Fire: "our logic for the clutter is flawed since we get floating clutter off the edge of the meshes"): a patch has one water level (its centre, else the highest on its shore
        // rings), so a bank beside a river stepping down a slope read as under water with none there. A water band now reads the water at its own spot, last of the gates, and a lily under its whole pad.
        private const float WaterTopSlack = 1f, OwnWaterMinDepth = 0.05f;
        private static long s_ownNoWater, s_ownOutOfBand, s_ownOverhang, s_ownTopGate, s_ownPlaced;
        private static float s_ownLogAt;
        private static bool s_ownProof;
        private static bool OwnWaterOn => VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.ClutterWaterAtOwnSpot != null && VerdantsAscent.Terrain.Voxel.FiresVoxelConfig.ClutterWaterAtOwnSpot.Value;
        internal static string OwnWaterStats() => $"no water at own spot {s_ownNoWater}, out of its depth band {s_ownOutOfBand}, lily overhang {s_ownOverhang}, above all water near (cheap gate) {s_ownTopGate}, placed {s_ownPlaced}";

        private static float PatchWaterTop(Builder b, float level)
        {
            if (!float.IsNaN(b.WaterTop)) return b.WaterTop;
            float top = level;
            for (int ix = -1; ix <= 1; ix++)
                for (int iz = -1; iz <= 1; iz++)
                    if (global::VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(b.Center.x + ix * b.HalfSize, b.Center.z + iz * b.HalfSize, OwnWaterMinDepth, true, out float s, out _) && s > top) top = s;
            return b.WaterTop = top;
        }

        // Water at all five pad points, each within 1 m of the centre's level (another body's level is not this pad's water). Best = the highest of them.
        private static bool PadOnWater(float x, float z, float centre, out float best)
        {
            best = centre;
            for (int i = 1; i < 5; i++)
            {
                float px = x + (i == 1 ? PadReach : i == 2 ? -PadReach : 0f);
                float pz = z + (i == 3 ? PadReach : i == 4 ? -PadReach : 0f);
                if (!global::VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(px, pz, OwnWaterMinDepth, true, out float s, out _) || Mathf.Abs(s - centre) > 1f) return false;
                if (s > best) best = s;
            }
            return true;
        }

        private static void NoteOwnWater()
        {
            if (!s_ownProof) { s_ownProof = true; FiresLogger.LogInfo("[WaterLife] lilies and vass need water at their own spot, lilies under the whole pad (0.2.1348)"); }
            if (!FiresLogger.DebugEnabled || Time.unscaledTime < s_ownLogAt) return;
            s_ownLogAt = Time.unscaledTime + 10f;
            FiresLogger.LogDebug($"[WaterLife] water clutter so far: {OwnWaterStats()} (0.2.1348)");
        }

        private static bool PlaceClutterType(ClutterSystem cs, Builder b, ClutterSystem.Clutter c, int index1,
            ClutterSystem.Quality quality, float amountScale, float waterLevel, GameObject grassRoot, float lodCap,
            Stopwatch sw)
        {
            if (!c.m_enabled || (b.Biomes & c.m_biome) == Heightmap.Biome.None) return true;
            float lilyLift = c.m_snapToWater && b.UnderwaterWide ? global::VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.LilyLift() : 0f;
            // A patch with no voxel edit anchor within reach skips the per-candidate edit lookup.
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
                // No grass floating over edited terrain: which vanilla gate refuses each candidate,
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
                // From here on, "edited" is the ground itself, off the generated height (FatGroundProbe.IsGroundEdited).
                edited = global::VerdantsAscent.Terrain.Voxel.FatGroundProbe.IsGroundEdited(point.x, point.z, point.y);
                bool waterBand = b.UnderwaterWide && (c.m_maxAlt < 0f || c.m_snapToWater);   // 0.2.1083: vass / lilies on our water, counted per gate
                if ((c.m_biome & biome) == Heightmap.Biome.None) { GateCount(edited, Gate.Biome); if (waterBand) WaterBandNote(c, 1); continue; }

                float alt = point.y - waterLevel;
                float maxAlt = b.UnderwaterWide ? VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.WideMaxAlt(c) : c.m_maxAlt;
                float minAlt = b.UnderwaterWide ? VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.WideMinAlt(c) : c.m_minAlt;   // 0.2.1086: lilies over any depth
                bool ownWater = waterBand && OwnWaterOn;   // 0.2.1348: judged at its own spot below; here only the cheap gate
                if (ownWater)
                {
                    if (point.y - (PatchWaterTop(b, waterLevel) + WaterTopSlack) > maxAlt) { s_ownTopGate++; GateCount(edited, Gate.Altitude); WaterBandNote(c, 2, alt, maxAlt); continue; }
                }
                else if (alt < minAlt || alt > maxAlt) { GateCount(edited, Gate.Altitude); if (waterBand) WaterBandNote(c, 2, alt, maxAlt); continue; }
                if (waterBand && !ownWater) WaterBandNote(c, 0);
                // 0.2.1081 (Fire: "i dont WANT grass in the rivers i just want vass or lillies"; "grass ferns and shrubs can be on
                // the shores out of the water, not in it"): in a patch on our water, a LAND band (not an underwater band, not a
                // surface band) is refused at every instance point that is under our water; the banks above the line keep it.
                if (b.UnderwaterWide && c.m_maxAlt >= 0f && !c.m_snapToWater && UnderOurWater(point)) { GateCount(edited, Gate.Altitude); s_landUnderWater++; continue; }
                if (normal.y < cosMaxTilt || normal.y > cosMinTilt) { GateCount(edited, Gate.Tilt); continue; }

                if (c.m_minOceanDepth != c.m_maxOceanDepth)
                {
                    // Vanilla's ocean depth is the ZONE's depth under the sea (Heightmap.UpdateCornerDepths: max(0, 30 - height)
                    // at the heightmap's four corners, lerped). It keeps a clutter such as instanced_waterlilies (ocean depth
                    // 0..1) out of the open sea, and it reads 0 at an inland pond, which is right as it is. Replacing
                    // it with the pond's local depth refused every lily deep enough for its altitude band
                    // (bed 3.2-4.5 m under the surface), so vanilla's stays.
                    float od = hmap.GetOceanDepth(pos);
                    if (od < c.m_minOceanDepth || od > c.m_maxOceanDepth) { GateCount(edited, Gate.OceanDepth); continue; }
                }
                // Layered clutter: on a cave floor the heightmap's mask is the SURFACE's above it; the cave's own hoe /
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
                // 0.2.1132 (Fire: no grass/snow clutter over a campfire's dry + wet rings): the melt core counts as cleared ground.
                if (global::VerdantsAscent.Terrain.Voxel.FatFireBullseye.HasHeat && global::VerdantsAscent.Terrain.Voxel.FatFireBullseye.InMeltCore(point))
                { global::VerdantsAscent.Terrain.Voxel.FatFireBullseye.ClutterRefused++; GateCount(edited, Gate.Cleared); continue; }
                // Cave clutter by light: in a cave the biome's clutter keeps 'Dark density' of its pieces at
                // 'Dark scale' where no light reaches, rising to all of them at full size toward mouths, shafts and sinkholes.
                float lightScale = 1f;
                if (caveFloor)
                {
                    if (float.IsNaN(b.Light)) b.Light = global::VerdantsAscent.Terrain.Voxel.CaveLight.At(point);
                    float keep = Mathf.Lerp(global::VerdantsAscent.Terrain.Voxel.CaveLight.DarkDensity, 1f, b.Light);
                    if (CandidateHash(pos, index1) >= keep) { GateCount(edited, Gate.Cleared); continue; }
                    lightScale = Mathf.Lerp(global::VerdantsAscent.Terrain.Voxel.CaveLight.DarkScale, 1f, b.Light);
                }
                // 0.2.1348: last, the dearest gate: the water at this instance's own spot (generated, placed or the sea).
                float ownSurface = float.NaN, padBest = float.NaN;
                if (ownWater)
                {
                    NoteOwnWater();
                    if (!global::VerdantsAscent.Water.Core.FatWaterChecks.TryWaterAt(point.x, point.z, OwnWaterMinDepth, true, out ownSurface, out _))
                    { s_ownNoWater++; GateCount(edited, Gate.Altitude); WaterBandNote(c, 2, alt, maxAlt); continue; }
                    float ownAlt = point.y - (ownSurface - VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.VanillaClutterGap);
                    if (ownAlt < minAlt || ownAlt > maxAlt) { s_ownOutOfBand++; GateCount(edited, Gate.Altitude); WaterBandNote(c, 2, ownAlt, maxAlt); continue; }
                    if (VerdantsAscent.Terrain.Voxel.ClutterSystem_GenerateVegPatch_WaterEcology.IsLily(c) && !PadOnWater(point.x, point.z, ownSurface, out padBest))
                    { s_ownOverhang++; GateCount(edited, Gate.Altitude); WaterBandNote(c, 2, ownAlt, maxAlt); continue; }
                    s_ownPlaced++;
                    WaterBandNote(c, 0);
                }
                GateCount(edited, Gate.Placed);

                pos = point;
                // 0.2.1090: on our water a lily sits above the shader's swell crest (FiresClutterWaterEcology.LilyLift), and its
                // random offset only lifts, so no pad dips into the swelling surface and z-fights. The RNG draw is kept either way.
                bool lily = c.m_snapToWater && b.UnderwaterWide;
                if (c.m_snapToWater) pos.y = waterLevel + (lily ? lilyLift : 0f);
                // 0.2.1091: a pad on placed water sits on PlacedWaterField at its own x,z (the drawn surface, which dips with the
                // bed and bank cap), not the patch's one sampled level.
                if (lily && !float.IsNaN(ownSurface)) pos.y = (float.IsNaN(padBest) ? ownSurface : padBest) + lilyLift;   // 0.2.1348: the surface already read, the pad's highest point
                else if (lily && global::VerdantsAscent.Water.Core.PlacedWaterField.TrySurface(pos.x, pos.z, waterLevel - 3f, waterLevel + 3f,
                        global::VerdantsAscent.Water.Core.PlacedWaterField.Consumer.Lily, out float padSurface, out _))
                    pos.y = padSurface + lilyLift;
                // 0.2.1094 (Fire: "lillies could still get bumped up a tiny bit ... on slopes at least"): on a sloped surface (a river
                // running downhill, a bank edge) the centre sample sits under the uphill side of the pad. The pad takes the highest
                // surface over its footprint (centre + 4 points at PadReach); on flat water that equals the centre, so no extra lift.
                if (lily && float.IsNaN(padBest)) pos.y = Mathf.Max(pos.y, FootprintSurface(pos.x, pos.z, pos.y - lilyLift) + lilyLift);
                if (c.m_randomOffset != 0f)
                {
                    float off = UnityEngine.Random.Range(-c.m_randomOffset, c.m_randomOffset);
                    pos.y += lily ? Mathf.Abs(off) : off;
                }

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

        // ── the gate census (bare grass over edited ground) ─────────────────────────────────────
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

        // Builders waiting, and frames the fill was held back by a queued heightmap rebuild (since the census reset).
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

        // An endless "InvalidOperationException: Instancing is not supported" came from
        // Graphics.DrawMeshInstanced in InstanceRenderer.CustomUpdate on a dedi. A headless server (-batchmode -nographics) cannot instance-draw;
        // vanilla's ClutterSystem skips its grass there (Awake returns on GraphicsDeviceType.Null), but clutter a mod spawns itself
        // (FAT's ClutterSpread builder, environment-box clutter, other mods) still brings InstanceRenderers. On a headless process an
        // InstanceRenderer never registers for CustomUpdate and is disabled; each source is logged once by name.
        [HarmonyPatch(typeof(InstanceRenderer), "OnEnable")]
        private static class HeadlessInstanceRenderer_Patch
        {
            private static readonly HashSet<string> s_logged = new HashSet<string>(StringComparer.Ordinal);
            private static int s_count;

            [HarmonyPrefix]
            private static bool Prefix(InstanceRenderer __instance)
            {
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null) return true;
                s_count++;
                try
                {
                    var t = __instance.transform; string path = t.name;
                    for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
                    string key = __instance.name.Replace("(Clone)", "");
                    if (s_logged.Add(key))
                        FiresLogger.LogInfo($"[FAT] headless server: InstanceRenderer '{key}' disabled (no instanced drawing without a GPU; {s_count} so far), first at {path}");
                }
                catch { }
                __instance.enabled = false;
                return false;
            }
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
