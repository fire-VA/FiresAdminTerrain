using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace VerdantsAscent.Terrain.Voxel
{
    // TRAIL OP BATCHING ([saddles] prep for [worldgen], the lead 10-28; round50 on FAT 0.2.1187: the client's [RpcCensus] top RPC
    // is TerrainComp.RPC_ApplyOperation - 868 ops in 48 windows = 1249 ms, 5.2 ms/s average, 1.44 ms/op, a 5 s window at
    // 39 ops = 80.5 ms - and FAT's own timer agrees ("received op handler ... ms avg 1.15 - 2.10"). In round50 those ops came
    // from the DEDI's [TrailNet] (0.5 - 3.6 ops/s, creature stamps: the player branch needs IsOwner) while the client's own
    // player trail sent ~0/s; the same path carries the player's stamps whenever it trenches.
    //
    // Vanilla applies every op alone: TerrainComp.DoOperation = InternalDoOperation + Save (the WHOLE zone's height + paint
    // arrays re-serialised into the ZDO, which then syncs to every peer) + Heightmap.Poke + ClutterSystem.ResetGrass. One trail
    // stamp = one TerrainOp = one routed RPC = one full Save + Poke + grass reset on the owner.
    //
    // Batched: a trail stamp (player walk, creature, both through FatSnowTrail / FatCreatureTrail) is queued per TerrainComp
    // instead of instantiated as a TerrainOp. Every FlushSeconds (or MaxPerBatch stamps) each comp's queue goes to the comp's
    // OWNER as ONE RPC (FatTrailOps); the owner runs, per item, the op guard (FatTerrainOpGuard.Allow) and vanilla's
    // InternalDoOperation (so FAT's G writer and every InternalDoOperation hook fire exactly as before) plus FAT's per-op
    // DoOperation hooks (paint scrubber, environment-box clutter mask, cultivated paint), then ONE Save, ONE Poke and ONE
    // ResetGrass over the batch's bounding circle. The local snow puff (TerrainOp.OnPlaced's effect) still plays at each stamp.
    // Off (config "Batch trail terrain ops" false, or an owner without this FAT version) = exactly the old per-stamp path.
    //
    // The [TrailBatch] summary line (Debug, every 10 s) times each stage of the owner's apply (guard / InternalDoOperation /
    // hooks / Save / Poke / grass) so the next round names where the remaining milliseconds per op go.
    internal static class FatTrailBatch
    {
        internal const string RpcName = "FatTrailOps";
        private const float FlushSeconds = 0.5f;
        private const int MaxPerBatch = 16, MaxReceive = 64;

        private sealed class Pending
        {
            public TerrainComp Comp;
            public readonly List<(Vector3 Pos, GameObject Prefab)> Items = new List<(Vector3, GameObject)>();
            public float Since;
        }

        private static readonly Dictionary<TerrainComp, Pending> s_queues = new Dictionary<TerrainComp, Pending>();
        private static readonly List<Heightmap> s_hms = new List<Heightmap>();
        private static readonly List<TerrainComp> s_drop = new List<TerrainComp>();
        private static readonly List<Pending> s_due = new List<Pending>();   // 0.2.1195: flushed after the loop
        private static int s_flushedByTick;

        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> NView = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> Hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, bool> Initialized = AccessTools.FieldRefAccess<TerrainComp, bool>("m_initialized");
        private static readonly Action<TerrainComp, Vector3, Vector3, TerrainOp.Settings> InternalDoOperation =
            AccessTools.MethodDelegate<Action<TerrainComp, Vector3, Vector3, TerrainOp.Settings>>(AccessTools.Method(typeof(TerrainComp), "InternalDoOperation"));
        private static readonly Action<TerrainComp, bool> Save =
            AccessTools.MethodDelegate<Action<TerrainComp, bool>>(AccessTools.Method(typeof(TerrainComp), "Save", new[] { typeof(bool) }));

        // Sent / received counters + per-stage owner timing for the summary line.
        private static int s_queued, s_batchesSent, s_itemsSent, s_batchesRecv, s_itemsRecv, s_refused, s_fallback;
        private static double s_msGuard, s_msInternal, s_msHooks, s_msSave, s_msPoke, s_msGrass, s_msTotal;
        private static float s_logAt;

        internal static bool Enabled => FiresVoxelConfig.TrailOpBatching != null && FiresVoxelConfig.TrailOpBatching.Value;

        // SENDER: queue a trail stamp instead of instantiating its TerrainOp prefab. False = not batched (the caller
        // instantiates as before): batching off, no TerrainOp on the prefab, or no terrain compiler under the stamp.
        internal static bool TryQueue(GameObject opPrefab, Vector3 pos)
        {
            if (!Enabled || opPrefab == null || ZNet.instance == null) return false;
            var op = opPrefab.GetComponent<TerrainOp>();
            if (op == null || TerrainOp.m_forceDisableTerrainOps) return false;
            s_hms.Clear();
            Heightmap.FindHeightmap(pos, op.GetRadius(), s_hms);   // vanilla TerrainOp.Awake's own routing (zone borders included)
            if (s_hms.Count == 0) { s_fallback++; return false; }
            float now = Time.unscaledTime;
            foreach (var hm in s_hms)
            {
                var comp = hm.GetAndCreateTerrainCompiler();
                if (comp == null) continue;
                if (!s_queues.TryGetValue(comp, out var q)) { q = new Pending { Comp = comp, Since = now }; s_queues[comp] = q; }
                q.Items.Add((pos, opPrefab));
                s_queued++;
                if (q.Items.Count >= MaxPerBatch) Flush(q);
            }
            op.m_onPlacedEffect?.Create(pos, Quaternion.identity);   // vanilla OnPlaced: the snow puff plays here and now
            return true;
        }

        // Called every frame (FatSnowTrail's tick owner, see the hook below): queues older than FlushSeconds go out.
        internal static void Tick()
        {
            if (s_queues.Count > 0)
            {
                // 0.2.1195 (live 1193 round51: "[TrailBatch] tick threw: Collection was modified; enumeration operation may not
                // execute." x98 from line 5601): Flush() removes its queue from s_queues, and it ran INSIDE the foreach over s_queues,
                // so every tick threw after the first due flush and the other due queues waited. Due queues are collected first, then
                // flushed after the loop.
                float now = Time.unscaledTime;
                s_drop.Clear();
                s_due.Clear();
                foreach (var kv in s_queues)
                {
                    if (kv.Key == null) { s_drop.Add(kv.Key); continue; }
                    if (now - kv.Value.Since >= FlushSeconds) s_due.Add(kv.Value);
                }
                foreach (var k in s_drop) s_queues.Remove(k);
                foreach (var q in s_due) Flush(q);
                if (s_due.Count > 0) s_flushedByTick += s_due.Count;
                s_due.Clear();
            }
            Summary();
        }

        private static void Flush(Pending q)
        {
            s_queues.Remove(q.Comp);
            if (q.Items.Count == 0 || q.Comp == null) return;
            var nview = NView(q.Comp);
            if (nview == null || !nview.IsValid())
            {
                // The comp went away while the stamps waited: fall back to vanilla's own path for each (it re-finds the comp).
                foreach (var it in q.Items) { UnityEngine.Object.Instantiate(it.Prefab, it.Pos, Quaternion.identity); s_fallback++; }
                return;
            }
            var pkg = new ZPackage();
            pkg.Write(q.Items.Count);
            foreach (var it in q.Items)
            {
                var op = it.Prefab.GetComponent<TerrainOp>();
                pkg.Write(it.Pos);
                pkg.Write(false);   // no rotation: trail ops are round paint stamps (vanilla writes m_rotation; trail prefabs have it off)
                op.m_settings.Serialize(pkg, it.Prefab);
            }
            nview.InvokeRPC(RpcName, pkg);   // to the ZDO owner, like vanilla's RPC_ApplyOperation (a local call when we own it)
            s_batchesSent++; s_itemsSent += q.Items.Count;
        }

        // RECEIVER registration: every TerrainComp's ZNetView gets the batch RPC (per instance, as vanilla registers its own).
        [HarmonyPatch(typeof(TerrainComp), "Awake")]
        private static class Patch_Register
        {
            private static void Postfix(TerrainComp __instance)
            {
                var nview = __instance != null ? NView(__instance) : null;
                if (nview == null) return;
                var comp = __instance;
                nview.Register<ZPackage>(RpcName, (sender, pkg) => Receive(comp, sender, pkg));
            }
        }

        // OWNER: apply a batch - per item the guard + vanilla's InternalDoOperation + FAT's per-op DoOperation hooks, then one
        // Save / Poke / ResetGrass. Mirrors vanilla DoOperation; FAT's DoOperation scope (trail-grass narrowing) brackets it.
        private static void Receive(TerrainComp comp, long sender, ZPackage pkg)
        {
            if (comp == null || pkg == null) return;
            var nview = NView(comp);
            if (nview == null || !nview.IsOwner() || !Initialized(comp)) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), t;
            double Lap(ref long since) { long now = System.Diagnostics.Stopwatch.GetTimestamp(); double ms = (now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; since = now; return ms; }
            int count;
            try { count = pkg.ReadInt(); } catch { return; }
            if (count <= 0 || count > MaxReceive) { s_refused += Math.Max(count, 0); return; }   // size cap (security: sender + size checked)
            FatShovelPatch.EnsureAll("batch receive");   // as FatShovelPatch's RPC_ApplyOperation prefix: op names must resolve
            FatTrailFast.ClearTrailGrass();
            bool allPaintOnly = true, any = false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            var hm = Hmap(comp);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3 pos; TerrainOp.Settings s;
                    try
                    {
                        pos = pkg.ReadVector3();
                        Vector3 rot = Vector3.zero;
                        if (pkg.ReadBool()) rot = pkg.ReadVector3();
                        s = TerrainOp.Settings.Deserialize(pkg);
                        t = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (s == null || !FatTerrainOpGuard.Allow(sender, pos, s, "receive batch")) { s_refused++; s_msGuard += Lap(ref t); continue; }
                        s_msGuard += Lap(ref t);
                        InternalDoOperation(comp, pos, rot, s);
                        s_msInternal += Lap(ref t);
                        // FAT's DoOperation postfixes, per op (FatVanillaHooks.TerrainComp_DoOperation + FatCultivateLumps)
                        Lifecycle.VanillaTerrainOpScrubber.OnDoOperation(pos, s);
                        global::VerdantsAscent.Utilities.EnvironmentBoxPatches.OnTerrainOperation(comp, pos, s);
                        FatCultivateLumps.OnDoOperation(comp, pos, s);
                        s_msHooks += Lap(ref t);
                    }
                    catch (Exception ex) { FiresLogger.LogDebug($"[TrailBatch] item {i}/{count} from {sender} skipped: {ex.GetType().Name}: {ex.Message}"); break; }
                    any = true;
                    allPaintOnly &= s.m_paintCleared && !s.m_level && !s.m_raise && !s.m_smooth;
                    float r = s.GetRadius();
                    min = Vector3.Min(min, pos - new Vector3(r, 0f, r));
                    max = Vector3.Max(max, pos + new Vector3(r, 0f, r));
                }
                if (!any) return;
                t = System.Diagnostics.Stopwatch.GetTimestamp();
                Save(comp, allPaintOnly);
                s_msSave += Lap(ref t);
                hm?.Poke(1, allPaintOnly);
                s_msPoke += Lap(ref t);
                if (ClutterSystem.instance != null)
                {
                    Vector3 c = (min + max) * 0.5f;
                    float radius = Mathf.Max(max.x - min.x, max.z - min.z) * 0.5f;
                    ClutterSystem.instance.ResetGrass(c, radius);   // narrowed by ClutterResetWatch when every item was a trail op
                }
                s_msGrass += Lap(ref t);
            }
            finally { FatTrailFast.ClearTrailGrass(); }
            s_batchesRecv++; s_itemsRecv += count;
            s_msTotal += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void Summary()
        {
            if (Time.unscaledTime < s_logAt) return;
            if (s_logAt > 0f && FiresLogger.DebugEnabled && (s_queued + s_itemsRecv + s_fallback) > 0)
            {
                double per = s_itemsRecv > 0 ? s_msTotal / s_itemsRecv : 0;
                FiresLogger.LogDebug($"[TrailBatch] 10 s: queued {s_queued} stamp(s), sent {s_itemsSent} in {s_batchesSent} batch(es); "
                    + $"applied here {s_itemsRecv} in {s_batchesRecv} batch(es), {s_msTotal:0.0} ms ({per:0.00} ms/op; guard {s_msGuard:0.0}, "
                    + $"InternalDoOperation {s_msInternal:0.0}, hooks {s_msHooks:0.0}, Save {s_msSave:0.0}, Poke {s_msPoke:0.0}, grass {s_msGrass:0.0}); "
                    + $"refused {s_refused}, fell back to single ops {s_fallback}; queues flushed by the timer {s_flushedByTick} (0.2.1195: collected, then flushed after the loop)");
            }
            s_logAt = Time.unscaledTime + 10f;
            s_queued = s_batchesSent = s_itemsSent = s_batchesRecv = s_itemsRecv = s_refused = s_fallback = s_flushedByTick = 0;
            s_msGuard = s_msInternal = s_msHooks = s_msSave = s_msPoke = s_msGrass = s_msTotal = 0;
        }
    }
}
