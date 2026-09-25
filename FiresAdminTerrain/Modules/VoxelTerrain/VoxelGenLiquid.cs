using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Water.Core;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Terrain.Voxel
{
    // -- GENERATED LIQUID - the hoe workflow, run automatically -----------
    //
    // The hand workflow that produces good water is three tools in sequence:
    //
    //   1. SPAWN  a liquid volume, over-filled for the space
    //   2. SETTLE for a tick or two - vanilla's own sim, not ours
    //   3. MOP    the thin spill that crept over the bank (it lies within
    //             centimetres of the ground and Z-fights)
    //   4. FREEZE it in place
    //
    // This does the same thing along a river's spine. Every step calls the
    // code the tools call - FiresLiquidWaterPrefab.SpawnSpring for the pour,
    // FiresLiquidGridOps.CullFlatSpill for the mop, FiresLiquidRiverController
    // .SetFrozen for the freeze. Nothing here re-implements a sim, seeds a
    // depth grid, or decides when water has settled; every previous attempt to
    // do that is what produced lumpy, half-settled, draining water.
    //
    // SpawnSpring rather than plain Spawn for two reasons: it attaches
    // FiresLiquidRiverController, which is what makes the surface follow the
    // channel banks downhill instead of levelling dead flat, and it is the
    // only pour the FREEZE tool can act on - the freeze command skips any
    // volume without that controller.
    internal static class VoxelGenLiquid
    {
        private const float POUR_SPACING = 14f;   // metres between pour centres along the spine
        private const float SAMPLE_STEP = 2f;     // spine search resolution
        private const float RADIUS_MIN = 5f;
        private const float RADIUS_MAX = 18f;
        // SETTLE TIME, in seconds - not frames.
        //
        // Vanilla does not begin simulating a fresh volume for a full second:
        // Update:247 only starts accumulating m_timeToSimulate once
        // m_updateDelayTimer passes 1.0, and the worker then steps 0.05s at a
        // time. Freezing 3 frames after the pour (~0.05s) therefore froze it
        // before a SINGLE sim step had run - so what got frozen was
        // InitializeLevels' raw centre blob, which is exactly the "barely a
        // puddle in the bottom of the channel" result. One second of dead time
        // plus real settling. A hand pour looks right at ~2.5-3s, but auto-pours
        // MERGE — the log shows 11 sources collapsing into 3 volumes — so each
        // body is far larger than a single hand pour and takes longer to level
        // across. 5s leaves ~4s of actual settling after vanilla's dead second.
        private const float SETTLE_MIN = 1.5f;      // vanilla does not simulate for its first ~1s
        private const float SETTLE_MAX = 20f;       // backstop so a body fed forever still freezes
        private const float SETTLE_POLL = 0.25f;    // how often each body's volume is sampled
        private const float SETTLE_FLAT = 0.004f;   // <0.4% volume change per poll counts as flat
        private const int SETTLE_FLAT_POLLS = 3;    // consecutive flat polls before freezing

        // How far under its bank the surface is seated. Deliberately NOT the
        // mesh backend's Water Bank Freeboard (0.45) — that number was tuned
        // for a meshed sheet, and at 0.45 the pours came in high: 48 "HEALED
        // over-bank source" corrections in one session, the worst dropping a
        // source 2.8m. The controller does catch them, but only AFTER pouring
        // too much, so the excess has to drain back out and reads as over-pour.
        // 1.0 is what the hand spring tool uses and what looks right.
        private const float BANK_OFFSET = 1.0f;
        // Deepest a pour may START above its own ground. The generator's water
        // level can sit 10m over the real terrain; without this the pour begins
        // as a blob that high and floods a bank on the way down.
        private const float MAX_POUR_DEPTH = 4f;
        // Mop thresholds - a cell thinner than this sitting on locally FLAT
        // ground is spill, not water. Ground with relief around it is a channel
        // and keeps its water at any depth (FiresLiquidGridOps.CullFlatSpill).
        private const float MOP_SHALLOW = 0.35f;
        private const float MOP_RELIEF = 0.6f;

        // -- UNREPRESENTABLE GROUND GUARD (every volume, ours and the hoe's) -
        //
        // UpdateHeights:383 clamps each cell's ground to +-m_maxDepth around the
        // volume, and a MISSED raycast defaults to exactly -m_maxDepth
        // (UpdateHeights:380). Both mean "this cell's ground is out of range",
        // but the sim keeps water there and the mesh draws it against the
        // clamped height. On mountain relief adjacent cells saturate at
        // OPPOSITE ends and the mesh spans them - a 20m vertical curtain of
        // water hanging between rock faces. Out-of-range ground holds no water.
        // From the postfix of FAT's one LiquidVolume.UpdateHeights hook (FatVanillaHooks), after the voxel surface pass.
        internal static void ClampGuard(LiquidVolume volume) => HeightClampGuard.Run(volume);

        private static class HeightClampGuard
        {
            internal static void Run(LiquidVolume __instance)
            {
                try
                {
                    var heights = FiresLiquidGridOps.Heights(__instance);
                    var depths = FiresLiquidGridOps.Depths(__instance);
                    if (heights == null || depths == null || heights.Count != depths.Count) return;
                    float edge = __instance.m_maxDepth - 0.05f;
                    int killed = 0;
                    for (int i = 0; i < heights.Count; i++)
                    {
                        if (depths[i] <= 0f) continue;
                        if (heights[i] <= -edge || heights[i] >= edge) { depths[i] = 0f; killed++; }
                    }
                    if (killed > 0) FiresLiquidGridOps.Dirty(__instance) = true;
                }
                catch { /* volume mid-teardown */ }
            }
        }

        // ── AUTO-POUR ────────────────────────────────────────────────────
        // Zones stream in and get poured automatically. Without this the only
        // generated water in the world is whatever someone typed
        // va_genliquid_spike over, which is how a fresh world came up bone dry
        // after the mesh backend was switched off.
        //
        // NOT server-gated. A LiquidVolume is a networked prefab: whoever
        // instantiates it creates the ZDO, it replicates, and every peer in
        // range renders it — which is exactly how the hoe tool works from a
        // client. Gating on IsServer() meant a client connected to a dedi never
        // poured anything, and the world came up dry with zero log lines.
        //
        // Duplicates are prevented by HasPourNear instead, which asks the WORLD
        // (replicated ZDOs) rather than a local set — so it also stops a zone
        // re-pouring its spine after a reload.
        private static readonly HashSet<Vector2s> s_queued = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_scratch = new List<Vector2s>();
        private static float s_nextPour;
        private const float POUR_INTERVAL = 0.2f;   // one zone per interval — streaming must not spike
        private const float FAR_DROP = 512f;        // queued zones beyond this are dropped, not poured

        // DEAD BY DOCTRINE: generated rivers/lakes/streams are the MESH —
        // the liquid engine is reserved for admin hammer placement only
        // (user mandate; auto-pours were double-placing sim water on gen
        // rivers). The UpdateHeights clamp guard above stays live for
        // hand-poured volumes.
        private static bool AutoEnabled => false;

        internal static void QueueZone(Vector2s zone)
        {
            if (!AutoEnabled || !GenWaterField.Ready) return;
            s_queued.Add(zone);
        }

        private static bool s_announced;
        private static int s_zonesTried, s_zonesPoured, s_volumesMade;

        internal static void Tick()
        {
            // Timed into the frame audit: the spine scan + Recentre reaches run
            // thousands of field samples on the main thread per poured zone.
            long __t0 = Stopwatch.GetTimestamp();
            try { TickBody(); }
            finally { Performance.FiresFrameAudit.AddPour(Stopwatch.GetTimestamp() - __t0); }
        }

        private static void TickBody()
        {
            if (!AutoEnabled || s_queued.Count == 0 || Time.time < s_nextPour) return;
            if (ZNet.instance == null) return;
            s_nextPour = Time.time + POUR_INTERVAL;

            if (!s_announced)
            {
                s_announced = true;
                FiresLogger.LogInfo($"[FiresGenLiquid] auto-pour ACTIVE — {s_queued.Count} zone(s) queued, " +
                          $"one every {POUR_INTERVAL:0.#}s. (server={ZNet.instance.IsServer()})");
            }

            // NEAREST FIRST. s_queued is a HashSet, so taking [0] pulled an
            // arbitrary zone — with a big queue the one you are standing in
            // could sit behind hundreds of others and only fill minutes after
            // you arrived. Pour outward from the player instead, and drop
            // anything that has streamed far away (it re-queues if you return).
            s_scratch.Clear();
            s_scratch.AddRange(s_queued);
            var pl = Player.m_localPlayer;
            Vector3 eye = pl != null ? pl.transform.position : Vector3.zero;
            var best = s_scratch[0];
            float bestD = float.MaxValue;
            for (int i = 0; i < s_scratch.Count; i++)
            {
                var z = s_scratch[i];
                float dx = (z.x * 64f) - eye.x, dz = (z.y * 64f) - eye.z;
                float d = dx * dx + dz * dz;
                if (d > FAR_DROP * FAR_DROP) { s_queued.Remove(z); continue; }
                if (d < bestD) { bestD = d; best = z; }
            }
            var zone = best;
            s_queued.Remove(zone);

            int n = PourZone(zone, out string report);
            s_zonesTried++;
            if (n > 0) { s_zonesPoured++; s_volumesMade += n; }

            // Report EVERY outcome, including "nothing here". Logging only on
            // success made silence ambiguous — a dry world looked identical
            // whether the pour was running and finding no water, or not running
            // at all. It was the second one, and that cost a test cycle.
            FiresLogger.LogInfo($"[FiresGenLiquid] auto-pour {report} " +
                      $"| totals: {s_zonesPoured}/{s_zonesTried} zone(s) wet, {s_volumesMade} volume(s), {s_queued.Count} queued");
        }

        private struct Spot { public float X, Z, Level, Radius; }

        private sealed class Runner : MonoBehaviour { }
        private static Runner s_runner;

        private static Runner GetRunner()
        {
            if (s_runner != null) return s_runner;
            var go = new GameObject("FiresGenLiquidRunner");
            Object.DontDestroyOnLoad(go);
            s_runner = go.AddComponent<Runner>();
            return s_runner;
        }

        // Half-width of the wet channel: expand outward until the generator
        // field goes dry. This is the "size between the banks" that decides how
        // much to pour - the hoe gets it from the ruler circle, we measure it.
        // Distance to the first dry cell along one direction.
        // -1 = NO bank found within RADIUS_MAX — the channel just keeps going
        // that way. Callers must not treat that as "the bank is at RADIUS_MAX":
        // sampling ground out there lands mid-channel downstream, which reads as
        // a bank far below the real rim.
        private static float Reach(float wx, float wz, float dx, float dz)
        {
            for (float s = SAMPLE_STEP; s <= RADIUS_MAX; s += SAMPLE_STEP)
                if (!GenWaterField.IsWetAt(wx + dx * s, wz + dz * s)) return s;
            return -1f;
        }

        private static float ReachOrMax(float wx, float wz, float dx, float dz)
        {
            float r = Reach(wx, wz, dx, dz);
            return r < 0f ? RADIUS_MAX : r;
        }

        // RECENTRE ON THE CHANNEL before pouring.
        //
        // The spine scan walks the zone row by row, so the first wet cell it
        // accepts in any row is the channel's EDGE — never its middle. Pouring
        // there puts half the volume on the bank, where it runs off instead of
        // filling the cut, which is exactly the "placed at the bank's edge, half
        // runs out" behaviour.
        //
        // Reach outward on both axes and step to the midpoint: if the wet span
        // runs 3m left and 11m right, the centre is 4m to the right. Two passes
        // is enough to converge on a channel of any width, and it fixes the
        // VOLUME too — Spawn integrates the basin under the disk, so a centred
        // disk with the true half-width asks for the right amount of water
        // instead of a bank-sized sliver.
        private static void Recentre(ref float wx, ref float wz, out float radius)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                float xp = ReachOrMax(wx, wz, 1f, 0f), xm = ReachOrMax(wx, wz, -1f, 0f);
                float zp = ReachOrMax(wx, wz, 0f, 1f), zm = ReachOrMax(wx, wz, 0f, -1f);
                float ox = (xp - xm) * 0.5f, oz = (zp - zm) * 0.5f;
                if (Mathf.Abs(ox) < 0.5f && Mathf.Abs(oz) < 0.5f) break;
                // Only step to somewhere still inside the channel.
                if (GenWaterField.IsWetAt(wx + ox, wz + oz)) { wx += ox; wz += oz; }
                else break;
            }
            float r = Mathf.Min(
                Mathf.Min(ReachOrMax(wx, wz, 1f, 0f), ReachOrMax(wx, wz, -1f, 0f)),
                Mathf.Min(ReachOrMax(wx, wz, 0f, 1f), ReachOrMax(wx, wz, 0f, -1f)));
            radius = Mathf.Clamp(r, RADIUS_MIN, RADIUS_MAX);
        }

        internal static int PourZone(Vector2s zone, out string report)
        {
            report = "";
            if (!GenWaterField.Ready) { report = "no generated water field (hydrology not ready?)"; return 0; }
            if (!FiresLiquidWaterPrefab.IsRegistered && !FiresLiquidWaterPrefab.Ensure())
            { report = "FiresLiquidWater prefab unavailable"; return 0; }

            var sw = Stopwatch.StartNew();
            float cx = zone.x * 64f, cz = zone.y * 64f;
            float bankOffset = BANK_OFFSET;

            var spots = new List<Spot>();
            float sp2 = POUR_SPACING * POUR_SPACING;
            for (float pz = cz - 32f; pz <= cz + 32f; pz += SAMPLE_STEP)
                for (float px = cx - 32f; px <= cx + 32f; px += SAMPLE_STEP)
                {
                    if (!GenWaterField.TryWaterSurfaceSmooth(px, pz, out float lvl, out float dep)) continue;
                    if (dep < 0.25f || lvl <= 30.25f) continue;
                    if (!GenWaterField.IsWetAt(px, pz)) continue;

                    // Centre on the channel FIRST. The spacing test and the fill
                    // cap both have to be evaluated where the pour will actually
                    // land — testing spacing at the raw scan point let two
                    // candidates 14m apart recentre onto nearly the same spot.
                    float cxw = px, czw = pz;
                    Recentre(ref cxw, ref czw, out float rad);

                    bool tooClose = false;
                    for (int i = 0; i < spots.Count && !tooClose; i++)
                    {
                        float ddx = spots[i].X - cxw, ddz = spots[i].Z - czw;
                        if (ddx * ddx + ddz * ddz < sp2) tooClose = true;
                    }
                    if (tooClose) continue;

                    // CAP THE FILL AT REAL GROUND, not the generator's level.
                    //
                    // lvl comes from the coarse gen water field, which routinely
                    // sits ABOVE the actual terrain — that is the same
                    // over-inclusive filled-basin field that produced 77 "HEALED
                    // over-bank source" corrections in one session. Handing it
                    // straight to Spawn as the fill level means the pour starts
                    // as a blob metres above the bank, drops, and floods over one
                    // side before it can equilibrate. The controller does heal it
                    // afterwards, but by then the damage is on screen.
                    //
                    // Bounding the START to ground + MAX_POUR_DEPTH means a pour
                    // can never begin above its own banks in the first place.
                    float gy;
                    if (!Heightmap.GetHeight(new Vector3(cxw + 0.013f, 0f, czw + 0.013f), out gy)) continue;
                    if (!GenWaterField.TryWaterSurfaceSmooth(cxw, czw, out float clvl, out _)) continue;

                    // FILL TO THE LOWEST BANK, not to a flat depth over the bed.
                    //
                    // The basin log showed the failure exactly:
                    //   "197/197 samples below fill, basin=959m3 deepest=4.9m"
                    // EVERY sample in the disk under the fill level — so the fill
                    // was above the banks too, and Spawn integrated the whole
                    // circle instead of the channel. That is the oversized pour:
                    // a ground+4m cap says nothing about whether the surrounding
                    // ground is 4m high or 40cm high.
                    //
                    // The banks are already known: Recentre walked out to the
                    // first dry cell on each axis, so sampling ground just past
                    // those is the rim. Water spills over the LOWEST of them, so
                    // that one sets the level.
                    // ONLY directions that actually found a bank. Where the
                    // channel runs on past RADIUS_MAX there is no rim that way,
                    // and sampling ground out there hits the bed DOWNSTREAM —
                    // metres below the real rim. Taking the min over that capped
                    // the fill under the bed, so the basin integrated to zero and
                    // Spawn fell back to its 5m³ floor:
                    //   "0/197 samples below fill, basin=0m³ -> pouring 5m³"
                    // which is a dry spot in an otherwise wet channel.
                    float bank = float.MaxValue;
                    for (int d = 0; d < 4; d++)
                    {
                        float bdx = d == 0 ? 1f : d == 1 ? -1f : 0f;
                        float bdz = d == 2 ? 1f : d == 3 ? -1f : 0f;
                        float rr = Reach(cxw, czw, bdx, bdz);
                        if (rr < 0f) continue;                 // no bank this way
                        rr += SAMPLE_STEP;
                        if (Heightmap.GetHeight(new Vector3(cxw + bdx * rr + 0.013f, 0f, czw + bdz * rr + 0.013f), out float bg)
                            && bg < bank) bank = bg;
                    }
                    float fill = Mathf.Min(clvl, gy + MAX_POUR_DEPTH);
                    if (bank < float.MaxValue) fill = Mathf.Min(fill, bank - BANK_OFFSET);
                    if (fill <= gy + 0.2f) continue;   // nothing worth pouring here
                    // Already poured — in this session or a previous one. Pours
                    // persist as ZDOs, so without this a zone re-pours its whole
                    // spine on every reload and stacks volumes forever.
                    if (FiresLiquidWaterPrefab.HasPourNear(new Vector3(cxw, fill, czw), POUR_SPACING * 0.75f)) continue;
                    spots.Add(new Spot { X = cxw, Z = czw, Level = fill, Radius = rad });
                }

            if (spots.Count == 0) { report = $"zone ({zone.x},{zone.y}) has no generated water"; return 0; }

            // ── LEVEL RECONCILIATION ─────────────────────────────────────
            // Each spot picked its fill from its OWN bank samples, so two pours
            // 14m apart in one channel disagree. They then merge, and the
            // controller takes the MAX fill over overlapping source disks
            // (_srcFillRel) — so the most generous sample wins and floods the
            // reach, while a spot that happened to sample a low bank sits under
            // its neighbours. That is the "some overflow, some settle too low"
            // pair, and no per-spot tuning fixes it because the disagreement IS
            // the bug.
            //
            // Clamp each spot to the LOWEST fill among its nearby spots: a
            // connected body spills at the lowest point it can reach, so no
            // section may sit above what its neighbours' banks allow. The
            // neighbourhood is deliberately LOCAL (2 pour spacings) — filtering
            // across the whole zone would flatten a downhill run to its outlet,
            // and rivers are supposed to fall along their length.
            //
            // Same min-filter that fixed the mesh backend's W-shaped surface.
            float lvlR = POUR_SPACING * 2f, lvlR2 = lvlR * lvlR;
            var reconciled = new float[spots.Count];
            for (int i = 0; i < spots.Count; i++)
            {
                float lo = spots[i].Level;
                for (int j = 0; j < spots.Count; j++)
                {
                    if (i == j) continue;
                    float ddx = spots[i].X - spots[j].X, ddz = spots[i].Z - spots[j].Z;
                    if (ddx * ddx + ddz * ddz > lvlR2) continue;
                    if (spots[j].Level < lo) lo = spots[j].Level;
                }
                reconciled[i] = lo;
            }
            float lowered = 0f;
            for (int i = 0; i < spots.Count; i++)
            {
                lowered += spots[i].Level - reconciled[i];
                var s2 = spots[i]; s2.Level = reconciled[i]; spots[i] = s2;
            }

            // SPAWN. center.y IS the fill level; the spawn probes the real
            // ground under it and integrates the basin to size the pour.
            //
            // SpawnSpring calls FindMergeTarget FIRST: a spring landing inside
            // an existing pour's grid is added to it as another SOURCE DISK -
            // one continuous mesh, no overlapping-sheet seam - instead of a
            // second LiquidVolume. So a spine of 6 pours usually returns the
            // SAME GameObject 6 times, with 6 sources on one controller. Dedupe
            // by instance, or the mop and freeze run six times over one volume
            // and the counts are fiction.
            // Volumes that existed BEFORE this batch. SpawnSpring returns the
            // MERGE TARGET when a source lands inside an existing pour's grid,
            // and treating that as "new" HID the neighbouring river: its
            // renderer went dark for the entire settle, which is exactly the
            // "travel downriver and the next section never renders / the water
            // vanishes" report. Only volumes born in this batch may be hidden.
            var pre = new HashSet<int>();
            foreach (var rcPre in Object.FindObjectsByType<FiresLiquidRiverController>(FindObjectsSortMode.None))
                pre.Add(rcPre.gameObject.GetInstanceID());

            var poured = new List<GameObject>(spots.Count);
            var seen = new HashSet<int>();
            var hide = new HashSet<int>();
            int merged = 0;
            foreach (var s in spots)
            {
                var go = FiresLiquidWaterPrefab.SpawnSpring(new Vector3(s.X, s.Level, s.Z), s.Radius, bankOffset);
                if (go == null) continue;
                int gid = go.GetInstanceID();
                if (seen.Add(gid))
                {
                    // SETTLE OUT OF SIGHT — but strictly batch-born volumes.
                    // Merge targets are live water already on screen; they stay
                    // visible while the new source settles into them.
                    if (!pre.Contains(gid))
                    {
                        hide.Add(gid);
                        foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                            mr.enabled = false;
                    }
                    poured.Add(go);
                }
                else merged++;
            }

            if (poured.Count > 0) GetRunner().StartCoroutine(SettleThenMopAndFreeze(poured, hide, zone));

            report = $"zone ({zone.x},{zone.y}) -> {spots.Count} source(s) along the spine " +
                     $"-> {poured.Count} volume(s) ({merged} merged into existing) " +
                     $"(spacing {POUR_SPACING:0}m, radius {RADIUS_MIN:0}-{RADIUS_MAX:0}m, bank offset {bankOffset:0.00}m, " +
                     $"reconciled -{(spots.Count > 0 ? lowered / spots.Count : 0f):0.00}m avg) " +
                     $"in {sw.ElapsedMilliseconds}ms - settling until flat (max {SETTLE_MAX:0}s), then mop + freeze.";
            return poured.Count;
        }

        private static IEnumerator SettleThenMopAndFreeze(List<GameObject> poured, HashSet<int> hide, Vector2s zone)
        {
            // GUARANTEED REVEAL. The body hides this batch's volumes while they
            // settle; if it dies mid-way (exception, volume torn down, coroutine
            // stopped with the runner) the water must never stay invisible —
            // that is the other half of the "section never renders" report.
            // finally survives every exit path: Unity disposes the iterator on
            // StopCoroutine/driver destruction, which runs it.
            var body = SettleBody(poured, hide, zone);
            try
            {
                while (body.MoveNext()) yield return body.Current;
            }
            finally
            {
                foreach (var g in poured)
                {
                    if (g == null) continue;
                    foreach (var mr in g.GetComponentsInChildren<MeshRenderer>(true))
                        if (!mr.enabled) mr.enabled = true;
                }
            }
        }

        private static IEnumerator SettleBody(List<GameObject> poured, HashSet<int> hide, Vector2s zone)
        {
            // 2. SETTLE - vanilla's sim, on its own thread.
            // A FIXED TIMER IS WRONG IN BOTH DIRECTIONS, and that is what
            // shipped. UpdateLiquid:308-314 zeroes the whole border row and
            // column EVERY step while the controller pins its sources to their
            // fill level, so a body runs toward equilibrium where feed rate
            // equals border drain rate. How long that takes depends on the
            // body's size and how soon its water reaches its own grid border:
            //
            //   small pour  -> hits its border early then keeps draining, so at
            //                  a fixed 5s it is PAST equilibrium: sits low and
            //                  never merges with its neighbour
            //   merged body -> still spreading at 5s, frozen mid-flow: rides
            //                  over the bank and leaves bumps
            //
            // Poll each volume's total water instead and freeze it the moment
            // its own change goes flat. Adaptive per body, no guessing.
            var pending = new List<GameObject>(poured);
            var lastVol = new Dictionary<int, float>();
            var steady = new Dictionary<int, int>();
            float t0 = Time.time;
            while (pending.Count > 0 && Time.time - t0 < SETTLE_MAX)
            {
                yield return new WaitForSeconds(SETTLE_POLL);

                // HOLD the renderers off every poll, not once at spawn.
                // Disabling once did not stick: the volume rebuilds its mesh
                // continuously while settling and comes back visible, so the
                // pour was still on screen going blob -> settle exactly as
                // before. Re-asserting each poll costs nothing and defeats any
                // re-enable, wherever it comes from.
                for (int i = 0; i < pending.Count; i++)
                {
                    var pg = pending[i];
                    if (pg == null) continue;
                    // Batch-born volumes only — a merge target mid-resettle is
                    // live water on screen and must never be blanked.
                    if (!hide.Contains(pg.GetInstanceID())) continue;
                    foreach (var mr in pg.GetComponentsInChildren<MeshRenderer>(true))
                        if (mr.enabled) mr.enabled = false;
                }

                if (Time.time - t0 < SETTLE_MIN) continue;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var g = pending[i];
                    if (g == null) { pending.RemoveAt(i); continue; }
                    var v = g.GetComponentInChildren<LiquidVolume>(true);
                    var dp = v != null ? FiresLiquidGridOps.Depths(v) : null;
                    if (dp == null) { pending.RemoveAt(i); continue; }
                    float vol = 0f;
                    for (int c = 0; c < dp.Count; c++) vol += dp[c];
                    int id = g.GetInstanceID();
                    if (lastVol.TryGetValue(id, out float prev))
                    {
                        float delta = Mathf.Abs(vol - prev) / Mathf.Max(1f, vol);
                        int s = steady.TryGetValue(id, out int cur) ? cur : 0;
                        steady[id] = delta < SETTLE_FLAT ? s + 1 : 0;
                        if (steady[id] >= SETTLE_FLAT_POLLS) pending.RemoveAt(i);
                    }
                    lastVol[id] = vol;
                }
            }
            float settleTook = Time.time - t0;
            int notConverged = pending.Count;

            int mopped = 0, frozen = 0;
            float culled = 0f;
            foreach (var go in poured)
            {
                if (go == null) continue;
                var lv = go.GetComponentInChildren<LiquidVolume>(true);
                if (lv == null) continue;

                // 3. MOP - the same flat-spill rule the mop tool uses. A thin
                // sheet on flat open ground lies within centimetres of the
                // terrain and Z-fights it; water in a banked cut is kept at any
                // depth because a channel has relief around it.
                var depths = FiresLiquidGridOps.Depths(lv);
                var heights = FiresLiquidGridOps.Heights(lv);
                var mutex = FiresLiquidGridOps.MeshDataLock(lv);
                if (depths != null && heights != null && mutex != null && System.Threading.Monitor.TryEnter(mutex))
                {
                    try
                    {
                        culled += FiresLiquidGridOps.CullFlatSpill(
                            depths, heights, lv.m_width + 1,
                            centerIdxX: 0, centerIdxZ: 0, srcHalf: -1,
                            shallowCull: MOP_SHALLOW, bankRelief: MOP_RELIEF);
                        FiresLiquidGridOps.Dirty(lv) = true;
                        mopped++;
                    }
                    finally { System.Threading.Monitor.Exit(mutex); }
                }

                // 4. FREEZE - hold the mesh, stop the overflow sim. Only pours
                // carrying a river controller can be frozen, which is why these
                // are spring pours.
                var river = go.GetComponent<FiresLiquidRiverController>();
                if (river != null)
                {
                    var nv = go.GetComponent<ZNetView>();
                    if (nv != null && nv.IsValid()) nv.ClaimOwnership();
                    river.SetFrozen(true);
                    frozen++;
                }

                // Reveal it settled. Unconditional — a volume whose controller
                // went missing must never be left permanently invisible.
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                    mr.enabled = true;
            }

            FiresLogger.LogInfo($"[FiresGenLiquid] zone ({zone.x},{zone.y}) settled {settleTook:0.0}s ({notConverged} still moving at cutoff) -> " +
                      $"mopped {mopped} pour(s) ({culled:0}m\u00B3 spill culled), froze {frozen}.");
        }

        internal static void Register()
        {
            new Terminal.ConsoleCommand("va_genliquid_spike",
                "[va_genliquid_spike] Auto-run the hoe workflow along your zone's river spine: SPAWN liquid volumes sized to the channel, SETTLE a few frames, MOP the thin spill, FREEZE in place.",
                args =>
                {
                    var p = Player.m_localPlayer;
                    if (p == null) { args.Context.AddString("no local player."); return; }
                    var zone = ZoneSystem.GetZone(p.transform.position);
                    int n = PourZone(zone, out string report);
                    string line = n > 0 ? $"va_genliquid_spike -> {report}" : $"va_genliquid_spike: {report}";
                    args.Context.AddString(line);
                    FiresLogger.LogInfo($"[FiresGenLiquid] {line}");
                }, isCheat: true);
        }
    }
}
