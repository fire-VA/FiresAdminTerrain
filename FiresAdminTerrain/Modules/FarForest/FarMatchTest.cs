using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using VerdantsAscent.Terrain.Voxel;

namespace VerdantsAscent.Terrain.FarForest
{
    // 0.2.1413 FarLands #2 (spec §5): MatchTest, the exactness proof of the replay. Host / single player only (it needs the location
    // instances and every ZDO). Every 30 s: each loaded zone around the local player and every peer that has been loaded >= 5 s and not
    // compared yet is replayed (FarReplay, main-thread path) and compared with its real ZDOs of the far kinds:
    //   reproduced = same prefab, position <= 0.05 m, rotation <= 1 deg, scale <= 0.01 (when the ZDO carries a scale);
    //   position-only = position matches, rotation or scale doesn't; extra = replayed, no real tree; missing = real tree, not replayed.
    // Felled or planted trees show as missing / extra, so the pass bar (>= 99 %) is read on fresh, untouched ground.
    // Debug line every pass (cumulative); Info once when it starts or stands down.
    internal static class FarMatchTest
    {
        private static float s_next;
        private static bool s_saidOn, s_saidClient;
        private static readonly Dictionary<Vector2s, float> s_firstSeen = new Dictionary<Vector2s, float>();
        private static readonly HashSet<Vector2s> s_done = new HashSet<Vector2s>();
        private static int s_zones, s_real, s_repro, s_posOnly, s_extra, s_missing, s_skippedNoHmap;
        private static double s_replayMs, s_replayWorstMs;
        private static readonly Dictionary<int, int[]> s_byKind = new Dictionary<int, int[]>();   // kind -> {real, repro, extra, missing, posOnly}
        private static readonly List<FarReplay.Tree> s_trees = new List<FarReplay.Tree>();
        private static readonly List<ZDO> s_zdos = new List<ZDO>();
        private static readonly List<Vector3> s_centres = new List<Vector3>();
        private const float SettleSeconds = 5f, PosTol = 0.05f, RotTolDeg = 1f, ScaleTol = 0.01f;

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class ZoneSystem_Update
        {
            private static void Postfix() { if (FarForest.MatchTest != null && FarForest.MatchTest.Value) Tick(); FarBridge.Tick(); }   // 0.2.1415: + the bridge
        }

        private static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_next) return;
            s_next = now + 30f;
            if (ZNet.instance == null || ZoneSystem.instance == null || ZDOMan.instance == null) return;
            if (!ZNet.instance.IsServer())
            {
                if (!s_saidClient) { s_saidClient = true; FiresLogger.LogInfo("[FarForest] MatchTest stands down on this peer: it runs on the host / dedicated server (it needs the location instances and every ZDO)."); }
                return;
            }
            if (FarForest.Kinds.Count == 0) return;
            if (!s_saidOn)
            {
                s_saidOn = true;
                FiresLogger.LogInfo($"[FarForest] MatchTest on: replay vs real trees in settled loaded zones, {FarForest.Kinds.Count} kind(s), every 30 s (Debug line); RNG self-test {(FarRngSelfTest.Trusted ? "passed" : "not passed")}, census words known for {FarForest.AwakeWords.Count} prefab(s).");
            }
            Centres();
            int zonesThisPass = 0;
            var zs = ZoneSystem.instance;
            foreach (var c in s_centres)
            {
                Vector2s cz = ZoneSystem.GetZone(c);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var z = new Vector2s(cz.x + dx, cz.y + dz);
                        if (s_done.Contains(z) || !zs.IsZoneLoaded(z)) continue;
                        if (!s_firstSeen.TryGetValue(z, out float t0)) { s_firstSeen[z] = now; continue; }
                        if (now - t0 < SettleSeconds) continue;
                        s_done.Add(z);
                        if (CompareZone(z)) zonesThisPass++;
                    }
            }
            if (zonesThisPass == 0) return;
            float pct = s_real > 0 ? 100f * s_repro / s_real : 0f;
            var worst = s_byKind.Where(kv => kv.Value[0] > 0).Select(kv => (k: kv.Key, miss: 100f * (kv.Value[0] - kv.Value[1]) / kv.Value[0], v: kv.Value))
                .OrderByDescending(t => t.miss).Take(5)
                .Select(t => $"{FarForest.Kinds[t.k].Prefab} {t.miss:0.#}% miss (real {t.v[0]}, repro {t.v[1]}, pos-only {t.v[4]}, extra {t.v[2]}, missing {t.v[3]}{(FarForest.AwakeWords.TryGetValue(FarForest.Kinds[t.k].Hash, out int aw) ? $", awake {aw}" : ", awake ?")})");
            FiresLogger.LogDebug($"[FarForest] match: {s_zones} zone(s) (+{zonesThisPass}), {pct:0.0}% reproduced ({s_repro} of {s_real} real), {s_posOnly} position-only, {s_extra} extra, {s_missing} missing; " +
                                 $"replay {(s_zones > 0 ? s_replayMs / s_zones : 0):0.00} ms/zone avg, worst {s_replayWorstMs:0.00}; {s_skippedNoHmap} zone(s) without a heightmap skipped; worst kinds: {string.Join("; ", worst)}");
        }

        private static void Centres()
        {
            s_centres.Clear();
            if (Player.m_localPlayer != null) s_centres.Add(Player.m_localPlayer.transform.position);
            foreach (var peer in ZNet.instance.GetPeers()) if (peer != null) s_centres.Add(peer.m_refPos);
        }

        private static bool CompareZone(Vector2s z)
        {
            Vector3 centre = ZoneSystem.GetZonePos(z);
            Heightmap hm = Heightmap.FindHeightmap(centre) ?? VoxelZoneStreamer.ZoneHeightmap(centre);
            if (hm == null) { s_skippedNoHmap++; return false; }
            s_trees.Clear();
            var st = new FarReplay.Stats();
            var sw = Stopwatch.StartNew();
            if (!FarReplay.ReplayZoneMain(z, hm, s_trees, st)) return false;
            double ms = sw.Elapsed.TotalMilliseconds;
            s_replayMs += ms; if (ms > s_replayWorstMs) s_replayWorstMs = ms;

            s_zdos.Clear();
            ZDOMan.instance.FindSectorObjects(z, new SimulationDistance(0, 0), s_zdos);
            var real = new List<(ZDO zdo, int kind)>();
            foreach (var zdo in s_zdos)
            {
                if (zdo == null || !FarForest.KindIndexByHash.TryGetValue(zdo.GetPrefab(), out int k)) continue;
                if (ZoneSystem.GetZone(zdo.GetPosition()) != z) continue;
                real.Add((zdo, k));
            }
            var used = new bool[real.Count];
            foreach (var t in s_trees)
            {
                int best = -1; float bestD = float.MaxValue;
                for (int i = 0; i < real.Count; i++)
                {
                    if (used[i] || real[i].kind != t.Kind) continue;
                    float d = Vector3.Distance(real[i].zdo.GetPosition(), t.Pos);
                    if (d < bestD) { bestD = d; best = i; }
                }
                var row = Row(t.Kind);
                if (best < 0 || bestD > PosTol) { s_extra++; row[2]++; continue; }
                used[best] = true;
                var zdo = real[best].zdo;
                bool rotOk = Quaternion.Angle(zdo.GetRotation(), t.Rot) <= RotTolDeg;
                Vector3 sc = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
                bool scaleOk = sc == Vector3.zero || Mathf.Abs(sc.x - t.Scale) <= ScaleTol;
                if (rotOk && scaleOk) { s_repro++; row[1]++; } else { s_posOnly++; row[4]++; }
            }
            for (int i = 0; i < real.Count; i++)
            {
                Row(real[i].kind)[0]++;
                if (!used[i]) { s_missing++; Row(real[i].kind)[3]++; }
            }
            s_real += real.Count;
            s_zones++;
            return true;
        }

        private static int[] Row(int kind)
        {
            if (!s_byKind.TryGetValue(kind, out var r)) s_byKind[kind] = r = new int[5];
            return r;
        }
    }
}
